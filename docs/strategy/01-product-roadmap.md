# GateSQL Product Roadmap

## Vision

GateSQL is the governance layer between AI agents and databases. The wire protocol proxy is the mechanism; the governance model is the product. Every feature should reinforce the positioning: **agents get full SQL expressiveness, operators get full control.**

---

## Layer 1: Session-Level Controls (Exists Today)

What's already built and differentiated:

- **JWT session auth** — Parent creates short-lived tokens via admin API. Agent uses JWT as `PGPASSWORD`. Agent never holds long-lived credentials.
- **Query budgets** — Hard cap on queries per session. Prevents runaway agents.
- **Idle timeout with sliding window** — Disconnect agents that go quiet (default 15min).
- **Hard cap expiry** — JWT `exp` = absolute session lifetime (default 8h).
- **Purpose enforcement** — Every query must include `/* <agent_purpose>reason</agent_purpose> */`. Creates an audit trail of *why* each query ran.
- **Query logging** — JSON-lines logs with timestamp, duration, purpose, and query text.
- **Dashboard** — MVC web UI showing sessions, queries, and budgets in real time.

### Gaps in Layer 1

| Feature | Why It Matters | Effort |
|---|---|---|
| **Read-only mode** | Most agent tasks are analytical. Write access when only reads are needed is unnecessary blast radius. Add `readOnly` flag to session creation; proxy rejects INSERT/UPDATE/DELETE/DROP/ALTER/TRUNCATE. | Small |
| **Connection string in session response** | Return `postgresql://agent:eyJ...@proxy:15432/mydb` alongside the token. Eliminates manual assembly — currently every integration reimplements the same string concatenation. | Small |
| **Better error messages** | Every error should include the fix. "Query rejected: missing purpose comment" should show a complete example query. LLMs also benefit — detailed errors let agents self-correct. | Small |

---

## Layer 2: Query-Level Controls (Next Frontier)

This is where GateSQL becomes unique. No other tool provides query-level governance for AI agents.

### Table & Schema Allowlists

Per-session configuration of accessible tables:

```json
{
  "agentId": "analyst-bot",
  "task": "Q1 revenue analysis",
  "queryBudget": 200,
  "allowedTables": ["public.orders", "public.products", "public.customers"]
}
```

**Why it matters:** Agents share a single upstream database user. You can't create a separate PG role for every agent session at scale. Allowlists provide defense-in-depth at the proxy layer — the agent physically cannot reference tables outside its scope. When a query references a denied table, the error message lists available tables, letting the agent self-correct.

### Dangerous Query Detection

Detect and handle destructive queries before they reach the database:

- `DROP TABLE`, `DROP DATABASE`
- `TRUNCATE`
- `DELETE` / `UPDATE` without `WHERE` clause
- `ALTER TABLE DROP COLUMN`

Three configurable modes:
- **`block`** — Reject with error message. Default for production.
- **`warn`** — Log and allow. Useful for monitoring without blocking.
- **`confirm`** — Reject unless query includes `/* confirmed:true */`. Gives the orchestrator a chance to review before allowing destructive operations.

**Why it matters:** LLMs hallucinate. A model that should update one row can generate `DELETE FROM orders` without a WHERE clause. The proxy is uniquely positioned to catch this because it sees every query before it hits the database.

### Automatic Row LIMIT Injection

If a SELECT has no LIMIT clause, auto-append `LIMIT 1000` (configurable per session). Send a PostgreSQL NOTICE message explaining what happened.

```
NOTICE: GateSQL added LIMIT 1000 to your query. Set a LIMIT explicitly to control result size.
```

**Why it matters:** Agents routinely generate `SELECT * FROM events` not realizing the table has 50M rows. This protects the database from expensive full-table scans AND the agent from receiving responses so large they blow the LLM's context window. A smart default that makes agents work better, not just safer.

### Query Cost Estimation Gate

Before forwarding a query, send `EXPLAIN (FORMAT JSON)` to get the estimated cost. Reject queries exceeding a configurable threshold.

```
ERROR: Query rejected — estimated cost 500,000 exceeds limit of 100,000.
HINT: Add WHERE clauses or LIMIT to reduce the query scope.
       Estimated rows: 12,400,000. Consider filtering by date or category.
```

**Why it matters:** This is *impossible* to build outside the proxy layer. The proxy has a live connection to the upstream database and can run EXPLAIN transparently. It prevents queries that would lock the database for minutes, costs almost nothing, and is a genuine structural advantage. "We run EXPLAIN on every query before your agent touches the database" is a killer feature for enterprise sales.

### Session Policies (Named Profiles)

Instead of specifying constraints on every API call, define reusable policies:

```json
// config.json
{
  "policies": {
    "analyst": {
      "readOnly": true,
      "queryBudget": 500,
      "allowedTables": ["public.orders", "public.products"],
      "maxRowsPerQuery": 1000,
      "dangerousQueryMode": "block"
    },
    "writer": {
      "readOnly": false,
      "queryBudget": 50,
      "allowedTables": ["public.orders"],
      "dangerousQueryMode": "confirm"
    }
  }
}
```

```bash
curl -X POST http://localhost:8080/api/sessions \
  -H "X-Api-Key: pk_dev_123" \
  -d '{"agentId": "my-agent", "task": "Q1 analysis", "policy": "analyst"}'
```

**Why it matters:** Reduces configuration duplication and error. The security team defines policies, the agent team references them by name. Changes propagate to all future sessions without code changes.

---

## Layer 3: Data-Level Controls (Enterprise Play)

These features justify Business/Enterprise pricing tiers and address compliance requirements.

### Automatic WHERE Clause Injection (Row-Level Filtering)

Per-session mandatory conditions injected transparently:

```json
{
  "agentId": "tenant-42-agent",
  "policy": "analyst",
  "rowFilters": {
    "orders": "tenant_id = 42",
    "customers": "tenant_id = 42"
  }
}
```

Every query on `orders` gets `AND tenant_id = 42` appended, regardless of how the agent writes it. The agent never knows the filter exists.

**Why it matters:** Critical for multi-tenant SaaS where each customer's agent should only see that customer's data. Works regardless of query complexity, without relying on PostgreSQL RLS (which requires per-user PG roles). More flexible than views because it works on any query pattern and can be changed per-session.

### Column Masking & Redaction

Per-session column redaction applied in DataRow messages before forwarding:

```json
{
  "maskedColumns": {
    "customers.email": "***@***.com",
    "customers.phone": "***-***-****",
    "customers.ssn": "REDACTED"
  }
}
```

The agent can reference customers by name or ID but never sees PII. The masking happens at the proxy level — the upstream query runs normally, results are modified in-flight.

**Why it matters:** Agents need to reference entities but shouldn't see sensitive data. This is cheaper and more flexible than database views. It works on any query pattern and can be configured per-session. This is the feature that gets you through compliance reviews (GDPR, HIPAA, SOC2).

### Session Hierarchies

Sessions can create sub-sessions with reduced permissions:

```
POST /api/sessions/{parentId}/children
{
  "agentId": "researcher-sub-agent",
  "policy": "analyst"  // must be equal or more restrictive than parent
}
```

Revoking the parent automatically revokes all children. Children cannot exceed parent permissions.

**Why it matters:** Maps directly to multi-agent architectures (CrewAI, AutoGen, LangGraph). An orchestrator agent with broad access spawns specialist sub-agents, each with minimum required permissions. The orchestrator's session is the security boundary — kill it and everything downstream stops.

---

## Layer 4: Observability & Integration

### Prometheus / OpenTelemetry Metrics

`/metrics` endpoint exposing:
- `gatesql_active_sessions` (gauge)
- `gatesql_queries_total` (counter, labels: agent, status)
- `gatesql_query_duration_seconds` (histogram)
- `gatesql_budget_utilization` (gauge, per session)
- `gatesql_bytes_proxied_total` (counter)
- `gatesql_upstream_connections` (gauge)

**Why it matters:** Enterprises already have Grafana/Datadog. They won't adopt a tool with a separate dashboard. A `/metrics` endpoint means GateSQL slots into existing observability stacks in 5 minutes. This is table stakes for any infrastructure tool.

### Real-Time Query Stream (SSE)

`GET /api/queries/stream` pushes query events via Server-Sent Events:

```
event: query
data: {"sessionId":"abc","agentId":"my-agent","query":"SELECT ...","duration_ms":42,"purpose":"analyzing orders"}
```

**Why it matters:** When debugging an agent, you need to see what it's doing *now*. SSE is simpler than WebSockets, sufficient for one-directional push, and works with `curl` for quick debugging.

### Webhook / Event System

Fire HTTP webhooks on configurable events:

```json
{
  "webhooks": [{
    "url": "https://my-app.com/hooks/gatesql",
    "events": ["session.created", "session.expired", "budget.exhausted", "query.rejected", "dangerous_query.blocked"]
  }]
}
```

**Why it matters:** Orchestrators need to react. "Budget exhausted" should trigger a grant-more-or-terminate decision. "Dangerous query blocked" should alert a human supervisor. Standard event-driven integration pattern.

### MCP Server Mode

GateSQL as an MCP (Model Context Protocol) server, exposing tools:

| Tool | Description |
|---|---|
| `query_database` | Run SQL through the governed proxy |
| `list_tables` | Return available tables (respecting allowlists) |
| `describe_table` | Return schema for a specific table |
| `get_session_info` | Remaining budget, time left, permissions |

**Why it matters:** This is potentially bigger than the wire protocol path. Any MCP client (Claude Desktop, Cursor, Windsurf, custom agents) gets governed database access with zero custom code. It meets AI tools where they already are. MCP server + wire protocol proxy = both integration patterns covered.

### Schema Discovery Endpoint

`GET /api/schema/{database}` introspects the upstream via `information_schema` and returns structured JSON:

```json
{
  "tables": [{
    "name": "orders",
    "schema": "public",
    "columns": [
      {"name": "id", "type": "integer", "nullable": false, "primaryKey": true},
      {"name": "customer_id", "type": "integer", "nullable": false, "foreignKey": "customers.id"},
      {"name": "total", "type": "numeric(10,2)", "nullable": false},
      {"name": "created_at", "type": "timestamp", "nullable": false}
    ],
    "indexes": ["idx_orders_customer_id", "idx_orders_created_at"]
  }]
}
```

**Why it matters:** The #1 reason agent SQL fails is schema misunderstanding. Today agents waste query budget running `\d` and `information_schema` queries. A dedicated endpoint lets the orchestrator fetch the schema once and inject it into the agent's prompt. Per-session schema context endpoint (`GET /api/sessions/{id}/context`) returns only the tables the agent is allowed to access — prompt-ready.

---

## Implementation Roadmap

### Phase 1: Table Stakes (Weeks 1-4)
1. Read-only mode per session
2. Connection string in session response
3. Better error messages (with examples and fix instructions)
4. Dangerous query detection (block mode)
5. Demo mode when no config provided (Docker default experience)

### Phase 2: Query Governance (Weeks 5-8)
6. Table/schema allowlists
7. Automatic row LIMIT injection
8. Session policies (named profiles)
9. Schema discovery endpoint
10. Prometheus `/metrics` endpoint

### Phase 3: Advanced Governance (Weeks 9-14)
11. Query cost estimation gate (EXPLAIN before forwarding)
12. Webhook/event system
13. Real-time query stream (SSE)
14. Connection pooling
15. MCP Server mode

### Phase 4: Enterprise (Weeks 15-24)
16. Automatic WHERE clause injection
17. Column masking/redaction
18. Session hierarchies
19. Multiple upstream databases
20. OAuth2/OIDC for admin API

### Phase 5: Ecosystem (Ongoing)
21. Python SDK
22. TypeScript SDK
23. LangChain/LangGraph toolkit
24. Terraform/Pulumi provider
25. Kubernetes Helm chart
