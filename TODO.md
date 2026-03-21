# TODO

## v2 — Query Cost & Impact Controls
- [ ] EXPLAIN cost gate — run EXPLAIN (no ANALYZE) before forwarding, reject queries above a configurable cost threshold. Estimates can be inaccurate (10x off with skewed data/stale stats) but good enough for catching obviously expensive queries. Consider: EXPLAIN every query vs heuristic pre-filter (AST-based: joins, no WHERE, etc.) to avoid doubling round-trips.
- [ ] Row impact limits for writes — use AST to extract table + WHERE clause from UPDATE/DELETE, run `SELECT COUNT(*) FROM <table> WHERE <condition>` before forwarding, reject if over limit (e.g. `maxAffectedRows: 1`). Implementation: use `Parser.Deparse()` to reconstruct the WHERE clause from the AST. This is the key differentiator — catches the subtle case where a query HAS a WHERE clause but still affects millions of rows (passes dangerous query detection but shouldn't). Edge case: in-transaction COUNT may not reflect uncommitted changes, but agents mostly use autocommit.
- [ ] Insert batch limits — for VALUES-based inserts, count items in `InsertStmt.selectStmt.valuesLists` via AST (no round-trip needed). For `INSERT ... SELECT`, fall back to COUNT approach like row impact limits.
- [ ] Automatic LIMIT injection — SELECT without LIMIT gets auto-appended LIMIT N (configurable per session). Use AST to detect missing LIMIT, then either rewrite SQL via `Parser.Deparse()` or reject with error telling agent to add one.
- [ ] Query timeout enforcement — kill queries that run longer than a per-session threshold
- [ ] Cost budget — per-session cumulative EXPLAIN cost budget alongside query count budget

## v2 — Permission Upgrades (Human-in-the-Loop)
- [ ] Agent hits permission boundary → proxy returns structured error with upgrade request
- [ ] Approval queue in dashboard (approve/deny with one click)
- [ ] Scoped approvals (this query only, this session, next N minutes, permanent)
- [ ] Slack integration for approval notifications
- [ ] Webhook system for approval events
- [ ] Auto-deny after configurable timeout
- [ ] Conditional approvals ("approved, but limit to 100 rows")
- [ ] Approval audit trail

## v2 — Access Control
- [ ] Column-level ACLs
- [ ] Row-level policies (transparent WHERE injection)
- [ ] Permission roles/groups (e.g., "report-reader", "data-writer") — session policies
- [ ] Connection string in session creation response

## v2 — Data Protection
- [ ] Column masking/redaction for PII
- [ ] Data classification tags (PII, financial, health)
- [ ] Data watermarking (trace which agent leaked data)

## v2 — Observability
- [ ] Prometheus / OpenTelemetry metrics endpoint (`/metrics`)
- [ ] Real-time query stream (SSE)
- [ ] Query fingerprinting & aggregate stats
- [ ] Per-agent cost attribution (EXPLAIN-based)

## v3 — Advanced
- [ ] MCP server integration
- [ ] Multi-database support
- [ ] Connection pooling
- [ ] Session hierarchies (parent/child with reduced permissions)
- [ ] OAuth2/OIDC for admin API
- [ ] mTLS as alternative auth mechanism
- [ ] Anomaly detection (alert on unusual access patterns)
- [ ] Agent behavior profiling (baseline "normal", flag deviations)
- [ ] Sandbox mode (run against DB snapshot, human approves before commit)
- [ ] Rollback-on-demand (undo agent's writes with one click)

## Done (v1.1)
- [x] Read-only sessions (`readOnly` flag)
- [x] Dangerous query detection (DROP, TRUNCATE, DELETE/UPDATE without WHERE) with block/warn/off modes
- [x] Table-level allowlists per session
- [x] AST-based query analysis via libpg_query (PostgreSQL's actual parser)
