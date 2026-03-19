# TODO

## v1 Polish
- [ ] Dashboard auth (currently open to anyone who can reach port 8080)
- [ ] AJAX refresh on dashboard instead of full page reload
- [ ] Upstream SSL/TLS (sslmode=require/verify-ca/verify-full)
- [ ] Publish Docker image to GHCR

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
- [ ] Table-level ACLs (which agents can access which tables)
- [ ] Column-level ACLs
- [ ] Row-level policies (transparent WHERE injection)
- [ ] Permission roles/groups (e.g., "report-reader", "data-writer")

## v2 — Query Safety
- [ ] DDL blocking (DROP, ALTER, CREATE)
- [ ] Mutation guards (require WHERE on UPDATE/DELETE)
- [ ] Query timeout enforcement
- [ ] Query complexity limits (EXPLAIN cost threshold)

## v2 — Data Protection
- [ ] Column masking/redaction for PII
- [ ] Data classification tags (PII, financial, health)
- [ ] Data watermarking (trace which agent leaked data)

## v3 — Advanced
- [ ] mTLS as alternative auth mechanism
- [ ] Anomaly detection (alert on unusual access patterns)
- [ ] Agent behavior profiling (baseline "normal", flag deviations)
- [ ] Sandbox mode (run against DB snapshot, human approves before commit)
- [ ] Rollback-on-demand (undo agent's writes with one click)
- [ ] MCP server integration
- [ ] Multi-database support
