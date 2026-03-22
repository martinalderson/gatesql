```
 ██████   █████  ████████ ███████ ┌──────────────────────────────┐
██       ██   ██    ██    ██      │ ███████  ██████  ██          │
██   ███ ███████    ██    █████   │ ██      ██    ██ ██          │
██    ██ ██   ██    ██    ██      │ ███████ ██    ██ ██          │
 ██████  ██   ██    ██    ███████ │      ██ ██ ██ ██ ██          │
                                  │ ███████  ██████  ███████     │
                                  └──────────────────────────────┘
```

**Secure PostgreSQL access for AI agents.**

GateSQL is an open-source PostgreSQL gateway that gives each AI agent a short-lived, scoped database session instead of raw credentials. You control what they can access, how much they can query, and every action is logged with intent.

[Website](https://gatesql.dev) &middot; [Quick Start](#quick-start) &middot; [Usage](#usage) &middot; [API Reference](#api-reference) &middot; [Configuration](#configuration) &middot; [Docker](#docker)

---

## Why GateSQL?

AI agents are most powerful when they can query your database directly. But giving them database credentials is a security nightmare: passwords get leaked, queries spiral out of control, and you have no idea *why* an agent ran a particular query.

GateSQL sits between the agent and your database as a native PostgreSQL wire protocol proxy. The entity spawning the agent (your app, orchestrator, CI pipeline) creates a session with specific permissions. The agent gets a short-lived JWT and connects with any standard PostgreSQL client. No custom SDK required.

```
┌───────────┐              ┌───────────┐              ┌────────────┐
│  AI Agent │──PG wire───► │  GateSQL  │──PG wire───► │ PostgreSQL │
│           │              │           │              │            │
│ psycopg   │              │ JWT auth  │              │ real creds │
│ asyncpg   │              │ policy    │              │ stay here  │
│ any client│              │ audit log │              │            │
└───────────┘              └───────────┘              └────────────┘
                                │
                           ┌────┴────┐
                           │ Admin   │
                           │ API +   │
                           │ Dashboard│
                           └─────────┘
```

## Features

- **Short-lived JWTs** - Parent creates a session, gets a JWT with hard expiry. Agent never sees real database credentials.
- **Read-only sessions** - Enforce read-only access per session. Writes are rejected at the proxy before reaching the database.
- **Table allowlists** - Restrict which tables an agent can access. Enforced via AST analysis, covering JOINs, subqueries, and CTEs.
- **Dangerous query detection** - Block DROP, TRUNCATE, DELETE without WHERE, UPDATE without WHERE. Configurable per session: block, warn, or allow.
- **Purpose enforcement** - Every query must include a purpose comment explaining *why*. Queries without one are rejected.
- **Query budgets** - Set a max query count per session. When the budget is exhausted, the connection is closed.
- **AST-powered analysis** - Every query parsed by PostgreSQL's actual parser ([libpg_query](https://github.com/pganalyze/libpg_query)). Catches CTEs with hidden writes, multi-statement injections, and more.
- **Full audit log** - JSON-lines query logs with agent ID, session ID, purpose, query text, and timing.
- **Admin API + Dashboard** - Create, list, and revoke sessions over HTTP. Live dashboard shows active sessions, queries, and governance rejections.
- **Idle & hard cap timeouts** - Sliding-window idle timeout (default 15 min) plus hard cap on total session lifetime (default 8 hours).
- **Standard clients** - Works with psql, psycopg, asyncpg, pgx, node-postgres, JDBC, or any PostgreSQL client.
- **Low overhead** - ~2.8x overhead vs direct PostgreSQL. Zero-allocation relay with ArrayPool, flush at sync points only.

## Database Support

| Database | Status | Notes |
|----------|--------|-------|
| PostgreSQL | Available | Full support. MD5, SCRAM-SHA-256, SSL/TLS. Versions 14+ tested. |
| ClickHouse | Available | Via ClickHouse's PostgreSQL-compatible wire interface. |
| MySQL | Coming soon | Native wire protocol proxy. |
| SQL Server | Coming soon | TDS protocol proxy. |

## Quick Start

GateSQL is designed to get you running in under a minute. No config file is needed for the first run — just start it, open the dashboard, and connect your database.

### 1. Start GateSQL

```bash
# Docker
docker run --rm -p 15432:15432 -p 8080:8080 gatesql/gatesql

# Or from source (.NET 10 SDK required)
git clone https://github.com/martinalderson/gatesql.git
cd gatesql
dotnet run --project src/DbProxy
```

On first startup, GateSQL automatically:
- Generates an API key and prints it to the terminal
- Opens a setup wizard in the dashboard at http://localhost:8080

### 2. Configure via the dashboard

Open http://localhost:8080 and the setup wizard will guide you through connecting your PostgreSQL database, with a test-connection button to verify it works.

### 3. Create your first session

Use the API key from the terminal output:

```bash
curl -X POST http://localhost:8080/api/sessions \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: <your-api-key>" \
  -d '{"agentId":"my-agent", "task":"data analysis", "queryBudget":100}'
```

The response includes a ready-to-use `connectionString` and `psqlCommand` — pass either one to your agent and it can connect immediately.

## Usage

GateSQL uses a parent/agent model. The **parent** (your app, orchestrator, or CI pipeline) creates sessions via the admin API. The **agent** connects using the session token as a PostgreSQL password.

### Step 1: Create a session

The parent calls the admin API to create a scoped session:

```bash
curl -X POST http://localhost:8080/api/sessions \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: your-api-key" \
  -d '{
    "agentId": "inventory-agent",
    "task": "check low-stock items",
    "readOnly": true,
    "queryBudget": 50,
    "allowedTables": ["public.inventory", "public.products"]
  }'
```

Response:

```json
{
  "token": "eyJhbGciOiJFUz...",
  "sessionId": "a1b2c3d4-...",
  "expiresAt": "2026-03-22T20:00:00Z",
  "connectionString": "postgresql://agent:eyJhbGciOiJFUz...@127.0.0.1:15432/postgres",
  "psqlCommand": "PGPASSWORD='eyJhbGciOiJFUz...' psql -h 127.0.0.1 -p 15432 -U agent -d postgres"
}
```

The `connectionString` and `psqlCommand` are ready to use directly - pass either one to your agent.

### Step 2: Agent connects and queries

The agent uses the token as a password with any PostgreSQL client. Every query must include a purpose comment explaining why:

```sql
/* <agent_purpose>check low-stock items for reorder alert</agent_purpose> */
SELECT product_name, quantity FROM inventory WHERE quantity < 10;
```

Queries without a purpose comment are rejected:

```
ERROR: Query rejected: all queries must include a purpose comment.
Add /* <agent_purpose>your reason here</agent_purpose> */ to your query.
```

### Step 3: Governance kicks in

GateSQL enforces the session's permissions in real time:

- **Table not in allowlist?** `ERROR: Access denied: table 'users' is not in the allowed tables list for this session.`
- **Write in a read-only session?** `ERROR: Query rejected: session is read-only. Only SELECT, EXPLAIN, and SHOW queries are allowed.`
- **Dangerous query?** `ERROR: Query rejected: dangerous query detected (DELETE without WHERE clause).`
- **Budget exhausted?** Connection is closed after the configured number of queries.
- **Session expired or idle?** Connection is terminated.

### Using with Python

```python
import psycopg

conn = psycopg.connect(response["connectionString"])

with conn.cursor() as cur:
    cur.execute("""
        /* <agent_purpose>check low-stock items for reorder alert</agent_purpose> */
        SELECT product_name, quantity FROM inventory WHERE quantity < 10
    """)
    rows = cur.fetchall()
```

### Using with Node.js

```javascript
import pg from "pg";

const client = new pg.Client({ connectionString: response.connectionString });
await client.connect();

const result = await client.query(`
  /* <agent_purpose>check low-stock items for reorder alert</agent_purpose> */
  SELECT product_name, quantity FROM inventory WHERE quantity < 10
`);
```

### Using with psql

```bash
PGPASSWORD='eyJhbGciOiJFUz...' psql -h 127.0.0.1 -p 15432 -U agent -d postgres
```

```sql
/* <agent_purpose>check low-stock items for reorder alert</agent_purpose> */
SELECT product_name, quantity FROM inventory WHERE quantity < 10;
```

## API Reference

All endpoints require the `X-Api-Key` header.

### Create session

```
POST /api/sessions
```

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `agentId` | string | Yes | | Identifier for the agent |
| `task` | string | Yes | | Description of what the agent is doing |
| `queryBudget` | int | No | unlimited | Max number of queries before the session is terminated |
| `readOnly` | bool | No | `false` | Only allow SELECT, EXPLAIN, and SHOW queries |
| `dangerousQueryMode` | string | No | `"block"` | How to handle dangerous queries: `"block"`, `"warn"`, or `"allow"` |
| `allowedTables` | string[] | No | all tables | List of tables the agent can access (e.g. `["public.orders"]`) |

**Response:** Returns `token`, `sessionId`, `expiresAt`, `connectionString`, and `psqlCommand`.

### List sessions

```
GET /api/sessions
```

Returns all sessions with their current status, query counts, and connection state.

### Revoke session

```
DELETE /api/sessions/{sessionId}
```

Immediately revokes the session. If the agent is connected, their next query will fail and the connection will be closed.

### Query logs

```
GET /api/queries?count=100
```

Returns recent query log entries with agent ID, session ID, task, query text, purpose, row count, duration, and success/error status.

## Configuration

GateSQL resolves configuration from multiple sources, applied in order of increasing priority:

1. **Built-in defaults** — sensible starting values for all settings
2. **config.json** — file-based configuration
3. **Environment variables** (`GATESQL_*`) — for Docker and CI
4. **Dashboard wizard** — saved to SQLite, highest priority

Each layer overrides the one before it. For example, if `config.json` sets the upstream host to `localhost` but `GATESQL_UPSTREAM_HOST` is set to `db.internal`, the environment variable wins. If you then configure a different host through the dashboard wizard, that takes highest priority. Wizard settings persist across restarts (stored in SQLite at `data/gatesql.db`).

API keys follow the same hierarchy, except that wizard-generated keys only apply when no keys are configured via `config.json` or environment variables — they never override explicitly-set keys.

In Docker, mount `/app/data` as a volume to preserve wizard settings across container restarts.

### Config file

Here's a full `config.json` example:

```json
{
  "proxy": {
    "listenPort": 15432,
    "listenHost": "0.0.0.0"
  },
  "upstream": {
    "host": "localhost",
    "port": 5432,
    "database": "postgres",
    "username": "postgres",
    "password": "postgres",
    "maxConnections": 20,
    "sslMode": "disable"
  },
  "auth": {
    "hardCapMinutes": 480,
    "idleTimeoutMinutes": 15,
    "signingKeyPath": "keys/signing.key",
    "parentApiKeys": [
      { "name": "my-orchestrator", "key": "pk_prod_abc123" }
    ]
  },
  "logging": {
    "directory": "logs"
  },
  "dashboard": {
    "enabled": true,
    "port": 8080
  }
}
```

### Config reference

| Section | Field | Default | Description |
|---------|-------|---------|-------------|
| `proxy` | `listenPort` | `15432` | Port the proxy listens on |
| `proxy` | `listenHost` | `0.0.0.0` | Address to bind to |
| `proxy` | `tlsCertPath` | `null` | TLS certificate for client-facing connections |
| `proxy` | `tlsKeyPath` | `null` | TLS key for client-facing connections |
| `upstream` | `host` | `localhost` | PostgreSQL host |
| `upstream` | `port` | `5432` | PostgreSQL port |
| `upstream` | `database` | `postgres` | Default database |
| `upstream` | `username` | | PostgreSQL username |
| `upstream` | `password` | | PostgreSQL password |
| `upstream` | `maxConnections` | `20` | Max upstream connections |
| `upstream` | `sslMode` | `disable` | SSL mode: `disable`, `prefer`, `require`, `verify-ca`, `verify-full` |
| `upstream` | `sslCaCertPath` | | CA cert path (required for `verify-ca`/`verify-full`) |
| `upstream` | `sslClientCertPath` | | Client cert for mTLS |
| `upstream` | `sslClientKeyPath` | | Client key for mTLS |
| `auth` | `hardCapMinutes` | `480` | Max session lifetime (8 hours) |
| `auth` | `idleTimeoutMinutes` | `15` | Idle timeout with sliding window |
| `auth` | `signingKeyPath` | `keys/signing.key` | ECDSA P-256 signing key (auto-generated on first run) |
| `auth` | `parentApiKeys` | | API keys for session creation |
| `logging` | `directory` | `logs` | Directory for JSON-lines query logs |
| `dashboard` | `enabled` | `true` | Enable the admin dashboard |
| `dashboard` | `port` | `8080` | Dashboard and API port |

### Environment variable overrides

All upstream settings can be overridden with environment variables, useful for Docker and CI:

| Variable | Description |
|----------|-------------|
| `GATESQL_UPSTREAM_HOST` | PostgreSQL host |
| `GATESQL_UPSTREAM_PORT` | PostgreSQL port |
| `GATESQL_UPSTREAM_USER` | PostgreSQL username |
| `GATESQL_UPSTREAM_PASSWORD` | PostgreSQL password |
| `GATESQL_UPSTREAM_DATABASE` | Default database |
| `GATESQL_UPSTREAM_SSLMODE` | SSL mode (`disable`, `prefer`, `require`, `verifyca`, `verifyfull`) |
| `GATESQL_UPSTREAM_SSL_CA_CERT` | CA cert path |
| `GATESQL_UPSTREAM_SSL_CLIENT_CERT` | Client cert path |
| `GATESQL_UPSTREAM_SSL_CLIENT_KEY` | Client key path |
| `GATESQL_API_KEY` | Sets a single API key |
| `GATESQL_DB_CONNECTION` | SQLite connection string for session storage |

## Purpose Enforcement

Every query sent through GateSQL must include a purpose comment that explains *why* the query is being run:

```sql
/* <agent_purpose>check low-stock items for reorder alert</agent_purpose> */
SELECT product_name, quantity FROM inventory WHERE quantity < 10;
```

This creates a full audit trail of not just *what* an agent did, but *why*. Internal driver queries (`pg_catalog` lookups, `SET`, `BEGIN`, `COMMIT`, etc.) are automatically exempt.

The purpose is logged alongside the query text, agent ID, session ID, timing, and row count in the JSON-lines query log.

## Dashboard

GateSQL includes a web dashboard at `http://localhost:8080` that shows:

- Active and expired sessions
- Live query feed with purposes
- Governance rejections (blocked queries, table violations, etc.)
- Session details with query history

> **Note:** The dashboard has no authentication. Restrict access to port 8080 via firewall rules or network policy.

## Docker

### Basic

```bash
docker run -p 15432:15432 -p 8080:8080 \
  -e GATESQL_UPSTREAM_HOST=host.docker.internal \
  -e GATESQL_UPSTREAM_PASSWORD=secret \
  -e GATESQL_API_KEY=pk_prod_abc123 \
  gatesql/gatesql
```

### With persistent data

```bash
docker run -p 15432:15432 -p 8080:8080 \
  -v gatesql-data:/app/data \
  -v gatesql-logs:/app/logs \
  -v gatesql-keys:/app/keys \
  -e GATESQL_UPSTREAM_HOST=host.docker.internal \
  -e GATESQL_UPSTREAM_PASSWORD=secret \
  -e GATESQL_API_KEY=pk_prod_abc123 \
  gatesql/gatesql
```

### With upstream SSL

```bash
docker run -p 15432:15432 -p 8080:8080 \
  -v /path/to/certs:/certs:ro \
  -e GATESQL_UPSTREAM_HOST=pg.example.com \
  -e GATESQL_UPSTREAM_PASSWORD=secret \
  -e GATESQL_UPSTREAM_SSLMODE=verifyfull \
  -e GATESQL_UPSTREAM_SSL_CA_CERT=/certs/ca.pem \
  -e GATESQL_API_KEY=pk_prod_abc123 \
  gatesql/gatesql
```

### Docker Compose

```yaml
services:
  gatesql:
    image: gatesql/gatesql
    ports:
      - "15432:15432"
      - "8080:8080"
    environment:
      GATESQL_UPSTREAM_HOST: postgres
      GATESQL_UPSTREAM_PASSWORD: secret
      GATESQL_API_KEY: pk_prod_abc123
    volumes:
      - gatesql-data:/app/data
      - gatesql-logs:/app/logs
      - gatesql-keys:/app/keys
    depends_on:
      - postgres

  postgres:
    image: postgres:17
    environment:
      POSTGRES_PASSWORD: secret
    volumes:
      - pgdata:/var/lib/postgresql/data

volumes:
  gatesql-data:
  gatesql-logs:
  gatesql-keys:
  pgdata:
```

## Development

### Build

```bash
dotnet build
```

### Run

```bash
dotnet run --project src/DbProxy -- config.json
```

### Test

```bash
dotnet test
```

Tests use [Testcontainers](https://testcontainers.com/) to spin up PostgreSQL in Docker. No local PostgreSQL install needed. The test suite runs against PostgreSQL versions 14-17.

### Benchmark

```bash
./scripts/benchmark.sh
```

Runs pgbench against both the proxy and direct PostgreSQL for comparison.

## License

[Business Source License 1.1](LICENSE) - Production use is permitted. The licensed work may not be offered as a commercial database proxy, access governance, or security gateway service. Converts to Apache 2.0 four years after each release.
