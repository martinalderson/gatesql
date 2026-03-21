# GateSQL Onboarding Strategy

## Benchmark

Resend. Clean docs, instant time-to-value, beautiful API playground, copy-paste examples that just work. The bar is: **the user sees value before they read any documentation.**

## The Current Experience

1. Clone the repo
2. Install .NET SDK
3. Run `dotnet build`
4. Find/create a `config.json`
5. Have a PostgreSQL instance running somewhere
6. Configure the upstream connection
7. Run `dotnet run --project src/DbProxy -- config.json`
8. Use curl to create a session (remember the API key, headers, JSON body)
9. Copy the token from the response
10. Set PGPASSWORD and connect with psql
11. Write a query with the purpose comment syntax (which you have to know about)
12. Open the dashboard to see what happened

This takes 15-30 minutes and requires PostgreSQL already running. Steps 5-7 are the biggest drop-off. Step 11 frustrates everyone who skips the docs.

## The Target Experience

```
$ docker run --rm -p 15432:15432 -p 8080:8080 gatesql/gatesql
```

One command. Everything works. 60 seconds to the "aha moment."

---

## The Zero-Config Demo Mode

When no config file is provided, GateSQL starts in demo mode automatically. Same image, same entrypoint — it just detects whether config exists.

**What happens in demo mode:**
1. Starts an embedded/bundled PostgreSQL with sample data
2. Generates a temporary API key
3. Creates a pre-loaded demo session
4. Prints a welcome banner with copy-paste commands

**What the user sees in their terminal:**

```
╔═══════════════════════════════════════════════════════════════════╗
║                                                                   ║
║   GateSQL v1.0 — Agent Database Gateway                          ║
║   Running in demo mode (no config.json detected)                 ║
║                                                                   ║
║   Dashboard:   http://localhost:8080                              ║
║   Proxy:       localhost:15432                                    ║
║   API Key:     demo_key_abc123                                   ║
║                                                                   ║
║   ── Try it ──────────────────────────────────────────────────── ║
║                                                                   ║
║   1. Query through the proxy:                                    ║
║                                                                   ║
║      PGPASSWORD="eyJhbG..." psql -h 127.0.0.1 -p 15432 \       ║
║        -U agent -d demo -c \                                     ║
║        "/* <agent_purpose>exploring demo</agent_purpose> */      ║
║         SELECT name, price FROM products ORDER BY price DESC     ║
║         LIMIT 5;"                                                ║
║                                                                   ║
║   2. Open the dashboard to see your query logged                 ║
║                                                                   ║
║   3. Create your own session:                                    ║
║                                                                   ║
║      curl -X POST http://localhost:8080/api/sessions \           ║
║        -H "Content-Type: application/json" \                     ║
║        -H "X-Api-Key: demo_key_abc123" \                         ║
║        -d '{"agentId":"my-agent","task":"testing"}'              ║
║                                                                   ║
║   Docs: https://gatesql.dev/quickstart                           ║
║                                                                   ║
╠═══════════════════════════════════════════════════════════════════╣
║   To use your own database, provide a config:                    ║
║   docker run -v ./config.json:/app/config.json gatesql/gatesql  ║
╚═══════════════════════════════════════════════════════════════════╝
```

**The sample database** should feel real — not `foo`/`bar`. An e-commerce dataset:
- `customers` (500 rows) — name, email, signup_date, tier
- `orders` (5,000 rows) — customer_id, total, status, created_at
- `products` (100 rows) — name, category, price, stock
- `order_items` (15,000 rows) — order_id, product_id, quantity, price

This lets agents write interesting queries: "top customers by revenue", "products trending this month", "average order value by tier". Real enough to be compelling, small enough to be fast.

**When someone runs with `-d` (detached):** They won't see the banner, but when they open `http://localhost:8080`, the dashboard shows the same welcome information — connection commands, sample queries, the demo API key.

---

## Error Messages as Onboarding

Every error the user encounters should teach them what to do next. This is the highest-impact, lowest-effort improvement — it's just string changes.

### Missing Purpose Comment

**Current:**
```
ERROR: Query rejected: missing purpose comment
```

**Better:**
```
ERROR: Query rejected — missing purpose comment.

Every query through GateSQL must include a purpose explaining why it's being run.
Add a SQL comment with the <agent_purpose> tag:

  /* <agent_purpose>analyzing Q1 revenue trends</agent_purpose> */
  SELECT customer_id, SUM(total) FROM orders
  WHERE created_at >= '2025-01-01'
  GROUP BY customer_id ORDER BY 2 DESC;

The purpose is logged for audit. It can be any text describing the agent's intent.
Docs: https://gatesql.dev/concepts/purpose-enforcement
```

### Invalid Token

**Current:**
```
ERROR: Invalid or expired session token
```

**Better:**
```
ERROR: Invalid or expired session token.

Sessions are short-lived. Create a new one via the admin API:

  curl -X POST http://localhost:8080/api/sessions \
    -H "Content-Type: application/json" \
    -H "X-Api-Key: <your-api-key>" \
    -d '{"agentId":"my-agent","task":"describe your task","queryBudget":100}'

Use the returned token as PGPASSWORD:

  PGPASSWORD="<token>" psql -h 127.0.0.1 -p 15432 -U agent -d <database>

Common causes:
  - Session expired (default: 8 hours)
  - Session went idle (default: 15 minutes of no queries)
  - Session was revoked via DELETE /api/sessions/{id}
  - Query budget was exhausted
```

### Budget Exhausted

**Current:**
```
ERROR: Query budget exceeded
```

**Better:**
```
ERROR: Query budget exhausted — 0 of 100 queries remaining.

This session has used all its allocated queries. To continue:

  1. Create a new session with a higher budget:
     curl -X POST http://localhost:8080/api/sessions \
       -H "X-Api-Key: <your-api-key>" \
       -d '{"agentId":"my-agent","task":"continued work","queryBudget":500}'

  2. Or check your usage in the dashboard: http://localhost:8080

Tip: Use queryBudget to prevent runaway agents. A typical analysis task
needs 20-50 queries. Set budgets based on task complexity.
```

### Upstream Connection Failed

**Current:**
```
ERROR: Failed to connect to upstream database
```

**Better:**
```
ERROR: Cannot connect to upstream PostgreSQL at localhost:5432.

Troubleshooting:
  1. Is PostgreSQL running?
     pg_isready -h localhost -p 5432

  2. Are the credentials correct?
     psql -h localhost -p 5432 -U postgres -d postgres

  3. Running in Docker? Use host.docker.internal instead of localhost:
     "upstream": { "host": "host.docker.internal", "port": 5432 }

  4. Check your config.json upstream settings:
     - host: localhost     (current)
     - port: 5432          (current)
     - username: postgres  (current)
     - database: postgres  (current)
```

---

## The Dashboard as Onboarding

### Empty State

When the dashboard has zero sessions, don't show an empty table. Show a welcome card:

```
┌──────────────────────────────────────────────────────────────┐
│                                                              │
│  Welcome to GateSQL                                          │
│                                                              │
│  Create your first agent session to get started.             │
│                                                              │
│  ┌─────────────────────────────────────────────────────┐     │
│  │  Agent ID:    [my-agent                         ]   │     │
│  │  Task:        [describe the task                ]   │     │
│  │  Budget:      [100                              ]   │     │
│  │                                                     │     │
│  │  [Create Session]                                   │     │
│  └─────────────────────────────────────────────────────┘     │
│                                                              │
│  Or via curl:                                                │
│                                                              │
│  curl -X POST http://localhost:8080/api/sessions \           │
│    -H "Content-Type: application/json" \                     │
│    -H "X-Api-Key: <your-api-key>" \                          │
│    -d '{"agentId":"my-agent","task":"my task"}'              │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

After session creation, show:
- The token (copy-to-clipboard button)
- The full connection string
- Code snippets in Python, TypeScript, and psql
- A "Test Connection" button that runs `SELECT 1` through the proxy and shows it in the query log

### Session Detail View

When viewing a session, show:
- Real-time query feed (updating as queries come in)
- Budget bar (visual progress: 47/100 queries used)
- Idle timer (counting up, threshold shown)
- Session metadata (agent ID, task, created at, expires at)
- "Revoke Session" button with confirmation

---

## The Quickstart Page

The single most important page on the docs site. Must work in under 5 minutes with only Docker as a prerequisite.

```markdown
# Quickstart

Get GateSQL running and query through it in under 5 minutes.

## Prerequisites

- Docker

That's it.

## 1. Start GateSQL

    docker run --rm -p 15432:15432 -p 8080:8080 gatesql/gatesql

GateSQL starts in demo mode with a sample database and prints
connection commands to your terminal.

## 2. Query through the proxy

Copy the psql command from the terminal output, or use this
(replace the token with the one shown in your terminal):

    PGPASSWORD="<token>" psql -h 127.0.0.1 -p 15432 -U agent -d demo -c \
      "/* <agent_purpose>quickstart demo</agent_purpose> */
       SELECT name, price FROM products ORDER BY price DESC LIMIT 5;"

You should see product results. The query went through GateSQL, which:
- Validated the JWT token
- Checked the session hadn't expired
- Verified the purpose comment was present
- Decremented the query budget
- Logged the query with timestamp and purpose

## 3. See what happened

Open http://localhost:8080 in your browser.

You'll see the session, the query, duration, purpose, and remaining budget.
Try running a few more queries and watch them appear in real time.

## 4. Try things that get blocked

    # Query without purpose comment — rejected
    PGPASSWORD="<token>" psql -h 127.0.0.1 -p 15432 -U agent -d demo -c \
      "SELECT * FROM products;"

    # After exhausting the budget — rejected
    # (run queries until the budget hits zero)

## 5. Create your own session

    curl -s -X POST http://localhost:8080/api/sessions \
      -H "Content-Type: application/json" \
      -H "X-Api-Key: demo_key_abc123" \
      -d '{"agentId":"my-second-agent","task":"testing budgets","queryBudget":5}' \
      | jq .

Use the token from the response to connect with a 5-query budget.
Watch it count down in the dashboard.

## 6. Revoke a session

    curl -X DELETE http://localhost:8080/api/sessions/<session-id> \
      -H "X-Api-Key: demo_key_abc123"

Try querying after revocation — the connection is terminated.

## What's next?

- [Connect to your own database →](/guides/connect-your-database)
- [Integrate with an AI agent →](/guides/first-agent)
- [Understand the security model →](/concepts/sessions)
```

---

## `gatesql init` Wizard

For users moving past the demo to their own database:

```
$ gatesql init

GateSQL Setup
─────────────

Checking for PostgreSQL...
  Found PostgreSQL on localhost:5432 ✓

Database connection:
  Host [localhost]:
  Port [5432]:
  Database [postgres]: myapp
  Username [postgres]:
  Password: ********

Testing connection... Connected ✓
  PostgreSQL 16.2 — 42 tables in public schema

GateSQL configuration:
  Proxy port [15432]:
  Dashboard port [8080]:

Generating API key... pk_gatesql_a7f3b2c1
Generating signing key... ✓

Config written to: ./config.json

Start GateSQL:
  docker run -v $(pwd)/config.json:/app/config.json \
    -p 15432:15432 -p 8080:8080 gatesql/gatesql

Or without Docker:
  dotnet run --project src/DbProxy -- ./config.json
```

---

## Progressive Disclosure

The onboarding reveals complexity gradually:

| Level | What the user sees | Features exposed |
|---|---|---|
| **Demo** | `docker run gatesql` — everything works | Sessions, purpose comments, query logging, dashboard |
| **Connected** | Pointed at their own database | All of the above + real data |
| **Configured** | Custom config.json | Query budgets, idle timeouts, hard caps, API keys |
| **Governed** | Session policies | Read-only mode, table allowlists, dangerous query detection |
| **Production** | Deployment guide | TLS, upstream SSL, Prometheus metrics, log shipping, key rotation |

Each level is documented separately. The user never sees production-hardening docs when they're in demo mode. But the path is always visible: "Next: connect your own database →"

---

## Template Gallery

Pre-built examples in `examples/` directory, each with its own `docker-compose.yml`:

### `examples/langchain-analyst/`
A Python agent using LangChain that answers natural language questions about the sample e-commerce database. Shows session creation, purpose-tagged queries, and budget management.

### `examples/openai-report-gen/`
A TypeScript agent using OpenAI function calling that generates weekly sales reports. Shows programmatic session management and structured output.

### `examples/multi-agent-pipeline/`
A CrewAI setup with two agents — a researcher (read-only, 200-query budget) and a writer (limited write, 20-query budget) — operating on separate sessions through the same proxy. Shows how session hierarchies model real multi-agent architectures.

### `examples/claude-data-analyst/`
Claude with tool use querying through GateSQL. Demonstrates the purpose enforcement flow — Claude naturally explains its intent, which becomes the purpose comment in the audit log.

Each example:
- Starts with `docker compose up`
- Includes a 2-minute demo GIF in the README
- Has inline comments explaining the GateSQL-specific parts
- Uses the same sample e-commerce dataset for consistency

---

## Docs Site

### Technology

Mintlify or Nextra. Both support:
- MDX with interactive code blocks
- Language-tabbed code examples (Python / TypeScript / curl)
- Clean, modern design out of the box
- Search
- Dark mode

### Structure

```
gatesql.dev/docs/
├── quickstart                  ← 5 minutes to working
├── concepts/
│   ├── why-gatesql             ← the philosophical case
│   ├── sessions                ← JWT lifecycle, expiry
│   ├── purpose-enforcement     ← why and how
│   └── query-budgets           ← hard caps
├── guides/
│   ├── connect-your-database   ← move past demo mode
│   ├── first-agent             ← connect an LLM agent
│   ├── langchain               ← LangChain integration
│   ├── openai                  ← OpenAI function calling
│   ├── claude                  ← Claude tool use
│   ├── production              ← TLS, SSL, monitoring
│   └── kubernetes              ← Helm chart, sidecar
├── comparisons/
│   ├── vs-raw-credentials      ← security argument
│   ├── vs-rest-api             ← expressiveness argument
│   └── vs-pg-rls               ← complementary, not competing
└── reference/
    ├── api                     ← OpenAPI spec
    ├── config                  ← every config.json field
    └── env-vars                ← all GATESQL_* overrides
```

### Principles
1. Every page starts with "what you'll be able to do" — not "what this is"
2. Every code example is copy-paste-run — no pseudo-code
3. The quickstart requires only Docker — no .NET, no PostgreSQL
4. Progressive complexity — simple first, power features later
5. Each API page: explanation on the left, code examples with language tabs on the right
