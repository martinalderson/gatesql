# DB Proxy

Secure PostgreSQL wire protocol proxy for AI agents. Agents connect with standard PG clients using short-lived JWTs as passwords.

## Tracking

GitHub Issues are for product/engineering work only (features, bugs, infra). Marketing, launch strategy, and business planning live in `docs/strategy/`.

## Build & Run

```bash
dotnet build
dotnet run --project src/DbProxy -- /home/martin/source/db-proxy/config.json
```

## Test

```bash
dotnet test                    # 83 tests: 36 unit + 47 integration (uses Testcontainers — requires Docker)
./scripts/restart-proxy.sh    # restart proxy (kills existing, starts fresh)
./scripts/benchmark.sh        # pgbench comparison: proxy vs direct
```

Tests use Testcontainers to spin up PostgreSQL in Docker. No local PG install needed. Version matrix tests run against PG 14–17.

## Architecture

The proxy speaks native PostgreSQL wire protocol. It does NOT use Npgsql for the proxy path — it implements the PG message framing directly (type byte + 4-byte big-endian length + payload). Npgsql is only used in the test suite as a client.

```
Agent ──PG wire──► Proxy (port 15432) ──PG wire──► PostgreSQL (port 5432)
Parent ──HTTP───► Admin API (port 8080)
Browser ──HTTP──► MVC Dashboard (port 8080)
```

### Key design decisions
- **Auth model**: Parent-delegated JWTs. The entity spawning the agent calls `POST /api/sessions` to get a short-lived JWT. Agent uses it as `PGPASSWORD`. Agent never holds long-lived credentials.
- **Session expiry**: JWT `exp` = hard cap (default 8h). Proxy-side idle timeout with sliding window (default 15min). Both enforced.
- **Purpose enforcement**: All queries must include `/* <agent_purpose>reason</agent_purpose> */`. Queries without it are rejected with an error message. Internal driver queries (pg_catalog, SET, BEGIN, etc.) are exempt.
- **Query governance**: AST-based query analysis via `libpg_query` (PostgreSQL's actual parser). Supports read-only sessions, dangerous query detection (DROP/TRUNCATE/DELETE without WHERE), and per-session table allowlists.
- **Upstream auth**: Proxy handles MD5 and SCRAM-SHA-256 password auth with upstream PG.
- **Upstream SSL/TLS**: Supports sslmode disable/prefer/require/verify-ca/verify-full for upstream connections.

### Performance
- Message relay uses `ArrayPool<byte>` — zero allocations in the hot path
- Flush only at protocol sync points (ReadyForQuery, ErrorResponse), not per-message
- Per-connection reusable 5-byte header buffers
- Session validation every 100 messages, not every message
- ~2.8x overhead vs direct PostgreSQL (986 TPS vs 2,720 TPS, 10 clients)

## Project structure

```
src/DbProxy/
  Protocol/          # PG wire protocol (reader, writer, handler, message types)
  Auth/              # JWT signing/validation, session manager
  Query/             # SQL comment parser, query logger, budget tracking, AST-based query analyzer
  Api/               # Admin API endpoints (session CRUD)
  Dashboard/         # MVC dashboard (Controllers, Views, Models)
  Configuration/     # Config model (maps to config.json)
config.json          # Runtime config (ports, upstream, auth keys, timeouts)
bench/               # pgbench scripts with purpose comments baked in
scripts/             # restart-proxy.sh, benchmark.sh
```

## API

All endpoints require `X-Api-Key` header matching a key in `config.json`.

```bash
# Create session
curl -X POST http://localhost:8080/api/sessions \
  -H "Content-Type: application/json" -H "X-Api-Key: pk_dev_123" \
  -d '{"agentId":"my-agent","task":"my-task","queryBudget":100,"readOnly":true,"dangerousQueryMode":"block","allowedTables":["public.orders","public.products"]}'

# List sessions
curl http://localhost:8080/api/sessions -H "X-Api-Key: pk_dev_123"

# Revoke session
curl -X DELETE http://localhost:8080/api/sessions/{sessionId} -H "X-Api-Key: pk_dev_123"

# Connect as agent
PGPASSWORD="<token>" psql -h 127.0.0.1 -p 15432 -U agent -d postgres
```

## Config

Config is JSON (`config.json`). Key fields:
- `proxy.listenPort` — port the proxy listens on (default 15432)
- `upstream.host/port/username/password` — real PostgreSQL connection
- `upstream.sslMode` — upstream SSL mode: disable (default), prefer, require, verify-ca, verify-full
- `upstream.sslCaCertPath` — CA cert path (required for verify-ca/verify-full)
- `upstream.sslClientCertPath/sslClientKeyPath` — optional client cert for mTLS
- `auth.hardCapMinutes` — max session lifetime (default 480 = 8h)
- `auth.idleTimeoutMinutes` — idle disconnect (default 15)
- `auth.parentApiKeys` — API keys for session creation
- `auth.signingKeyPath` — ECDSA P-256 key (auto-generated on first run)
- `dashboard.port` — HTTP port for admin API + dashboard (default 8080)
- `logging.directory` — JSON-lines query logs

## Gotchas
- Signing key is generated relative to `dotnet run` working directory (usually `src/DbProxy/`). Use absolute paths in config for predictability.
- Logs directory is also relative. Same advice.
- The dashboard has no auth — it's accessible to anyone who can reach port 8080.
- pgbench scripts in `bench/` include purpose comments; standard pgbench built-in scripts will be rejected.

## CI/CD

- **CI** (`.github/workflows/ci.yml`) — runs on PRs and pushes to main. Self-hosted runner on mini PC, .NET 10 pre-installed at `/home/github-runner/.dotnet`.
- **Docker Build** (`.github/workflows/docker.yml`) — pushes `gatesql/gatesql` to Docker Hub on pushes to main and version tags (`v*`). Requires `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` secrets.

## Environment variable overrides

All upstream config can be set via env vars (useful for Docker/CI):
- `GATESQL_UPSTREAM_HOST`, `GATESQL_UPSTREAM_PORT`, `GATESQL_UPSTREAM_USER`, `GATESQL_UPSTREAM_PASSWORD`, `GATESQL_UPSTREAM_DATABASE`
- `GATESQL_UPSTREAM_SSLMODE` — disable, prefer, require, verifyca, verifyfull
- `GATESQL_UPSTREAM_SSL_CA_CERT`, `GATESQL_UPSTREAM_SSL_CLIENT_CERT`, `GATESQL_UPSTREAM_SSL_CLIENT_KEY`
- `GATESQL_API_KEY` — sets a single API key
- `GATESQL_DB_CONNECTION` — SQLite connection string
