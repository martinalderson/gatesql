# GateSQL Roadmap

## Done

- **Read-only sessions** — `readOnly` flag on session creation; proxy rejects writes via AST analysis
- **Dangerous query detection** — DROP, TRUNCATE, DELETE/UPDATE without WHERE. Configurable: block/warn/off
- **Table allowlists** — Per-session list of accessible tables, enforced at the proxy layer
- **AST-based query analysis** — Every query parsed by libpg_query (PostgreSQL's actual parser). Handles CTEs with hidden writes, multi-statement queries, subqueries, schema-qualified tables

---

## Tier 1: Ship Before Public Launch

The query governance moat + first-run experience.

### Row impact limits for writes
Estimate affected rows before executing UPDATE/DELETE. AST extracts the WHERE clause and target table, proxy runs `SELECT COUNT(*) FROM <table> WHERE <condition>` against upstream, rejects if over the session's `maxAffectedRows` limit.

The feature nobody else has. Catches the subtle case: `UPDATE orders SET status = 'cancelled' WHERE created_at > '2020-01-01'` — has a WHERE clause, passes dangerous query detection, but affects 2 million rows.

### EXPLAIN cost gate
Run `EXPLAIN (FORMAT JSON)` before forwarding. Reject queries exceeding a configurable cost threshold. Fast (no execution), gives estimated cost from PG's planner. Estimates can be inaccurate with skewed data, but catches obviously expensive queries. Consider heuristic pre-filter (only EXPLAIN queries with joins, no WHERE, etc.) to avoid doubling round-trips on simple queries.

### Insert batch limits
For VALUES-based inserts, count items in the AST (zero cost — no round-trip). For `INSERT ... SELECT`, fall back to COUNT approach. Configurable `maxInsertRows` per session.

### Automatic LIMIT injection
SELECT without LIMIT gets auto-appended `LIMIT N` (configurable per session, e.g. 1000). Protects agents from pulling 50M rows into their context window. Send a PG NOTICE explaining what happened.

### Connection string in session response
Return `postgresql://agent:eyJ...@proxy:15432/db` alongside the token in the session creation response. Eliminates manual string assembly from every integration.

### Session policies (named profiles)
Define reusable profiles in config (`"analyst"`, `"writer"`) with preset readOnly, allowedTables, maxAffectedRows, etc. Sessions reference by name instead of repeating constraints per API call.

### Better error messages
Every proxy error includes the fix. Missing purpose comment shows a complete example query. Invalid token shows the curl to create a session. Budget exhausted shows how to create a new session. String changes only — highest impact, lowest effort.

### Demo mode (zero-config Docker)
When no config.json is provided, GateSQL starts in demo mode: bundled sample data, generated API key, pre-created session, welcome banner with copy-paste commands. `docker run gatesql/gatesql` just works.

---

## Tier 2: First Month After Launch

Production-readiness and ecosystem integration.

### Prometheus / OpenTelemetry metrics
`/metrics` endpoint: active sessions, QPS, duration histograms, budget utilization, bytes proxied. Table stakes for enterprise adoption — slots into existing Grafana/Datadog in 5 minutes.

### Webhook / event system
Fire HTTP webhooks on configurable events: session.created, session.expired, budget.exhausted, query.rejected, dangerous_query.blocked. Enables reactive orchestration.

### Query timeout enforcement
Kill queries exceeding a per-session threshold. Agents are unreliable — they generate queries that lock the database for minutes. The proxy can cancel the upstream query and return an error.

### Real-time query stream (SSE)
`GET /api/queries/stream` pushes query events via Server-Sent Events. When debugging an agent, you need to see what it's doing now.

### Cost budget
Per-session cumulative EXPLAIN cost budget alongside query count budget. Because `SELECT 1` and a 5-table join both count as "1 query" but differ by 10,000x in resource consumption.

### Query fingerprinting & aggregate stats
Normalize queries (replace literals with $1, $2), expose `/api/queries/stats` grouped by fingerprint: count, avg duration, p95. Per-agent pg_stat_statements without the PG extension.

### MCP server mode
GateSQL as an MCP server with `query_database`, `list_tables`, `describe_table`, `get_session_info` tools. Instant compatibility with Claude Desktop, Cursor, and every MCP client. Potentially bigger than the wire protocol path.

---

## Tier 3: Growth Features

What closes deals and expands accounts.

### Column-level ACLs
Per-session column restrictions. Agent can query `orders` but only see `id`, `total`, `created_at` — not `customer_email`.

### Row-level policies (WHERE injection)
Auto-inject `AND tenant_id = 42` into every query on specified tables. Critical for multi-tenant SaaS. Agent never knows the filter exists.

### Column masking / redaction
Replace PII in DataRow messages before forwarding. `customers.email` → `***@***.com`. Works on any query pattern, configurable per session.

### Connection pooling
Pool upstream PG connections instead of 1-per-agent. PostgreSQL falls over well before 100 concurrent connections. PgBouncer built into GateSQL.

### Permission roles / groups
Named permission sets that sessions inherit. The security team defines roles, the agent team references them.

### Human-in-the-loop approval queue
Agent hits a permission boundary → proxy returns structured error with upgrade request. Dashboard shows approve/deny queue. Scoped approvals: this query only, this session, next N minutes. Auto-deny after timeout.

### Slack integration for approvals
Notify on approval requests. Approve/deny from Slack without opening the dashboard.

---

## Tier 4: Enterprise

Features that justify $20K+/year contracts.

### Session hierarchies
Parent sessions create child sessions with reduced permissions. Revoking parent revokes all children. Maps directly to multi-agent architectures (CrewAI, AutoGen, LangGraph).

### OAuth2 / OIDC for admin API
Replace static API keys with bearer tokens validated against Auth0/Okta/Keycloak. Per-user audit trails.

### Multi-database support
Single GateSQL instance routing to multiple upstream PostgreSQL databases based on session config.

### Data classification tags
Label columns as PII, financial, health. Enforce policies based on classification — agents with "analyst" role can't access PII-tagged columns.

### Data watermarking
Inject invisible markers into query results that trace back to the agent session. If data leaks, you know which agent accessed it.

### Anomaly detection
Alert on unusual access patterns. "Agent X normally runs 20 queries/session but just ran 500" or "Agent Y accessed a table it's never touched before."

### Agent behavior profiling
Baseline "normal" query patterns per agent. Flag deviations automatically.

---

## Tier 5: Ambitious

Long-term differentiation.

### Sandbox mode
Run agent queries against a database snapshot. Human reviews the changes, approves, and they're applied to production. Full preview-before-commit workflow.

### Rollback-on-demand
Undo an agent's writes with one click. Track all mutations per session, generate reverse SQL.

### mTLS as alternative auth
Mutual TLS for environments where JWT-as-password doesn't fit.
