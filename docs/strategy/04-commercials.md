# GateSQL Commercial Strategy

## Core Positioning

GateSQL is not a database proxy — it is a **governance layer for AI agent database access**. This framing matters because "proxy" sounds like PgBouncer (commodity, never monetized), while "governance layer" sounds like Teleport or HashiCorp Vault (premium, enterprise-grade, high willingness to pay).

The purpose enforcement and audit logging features are the moat, not the wire protocol proxy. Every feature and pricing decision should reinforce the governance/security positioning.

---

## License: BSL 1.1 (Business Source License)

### Why BSL

| License | Pros | Cons | Verdict |
|---|---|---|---|
| MIT / Apache 2.0 | Maximum adoption, no friction | Any cloud provider can offer it as a managed service | Too permissive for a proxy — trivially wrappable |
| AGPL | Copyleft prevents cloud wrapping | Many enterprises have blanket AGPL bans — kills adoption | Too restrictive |
| **BSL 1.1** | **Free for all self-hosted/internal use. Prevents competitors from reselling as a service. Converts to Apache 2.0 after 4 years.** | Some OSS purists object (not "true" open source by OSI definition) | **Right balance** |

### Precedent

BSL is battle-tested by infrastructure companies:
- **HashiCorp** — Terraform, Vault, Consul. Acquired by IBM for $6.4B.
- **CockroachDB** — $633M raised.
- **Sentry** — Error monitoring. $217M raised.
- **Neon** — Serverless PostgreSQL. $104M raised.
- **MariaDB** — BSL for enterprise features.

### Additional Use Grant

The BSL allows you to define what IS permitted. Recommended grant: "You may use the Licensed Work for any purpose except offering it as a commercial database proxy or database access governance service." This means:
- Companies using GateSQL internally: **allowed** (free)
- Consultants deploying GateSQL for clients: **allowed** (free)
- AWS launching "Amazon GateSQL": **not allowed** (must license)
- A competitor forking and selling "BetterGateSQL Cloud": **not allowed** (must license)

### Conversion

BSL converts to Apache 2.0 after 4 years (the "Change Date"). This builds long-term community trust — people know that even if the company disappears, the code becomes fully open. This is a meaningful differentiator from proprietary alternatives.

---

## Business Model: Open-Core + Managed Service

### What's Open (BSL)

Everything currently in the repo:
- PostgreSQL wire protocol proxy
- JWT session auth
- Purpose enforcement
- Query budgets and idle timeouts
- JSON-lines query logging
- Basic MVC dashboard
- Docker image
- Single-node operation
- Admin API (session CRUD)
- MD5 and SCRAM-SHA-256 upstream auth
- Upstream SSL/TLS support

### What's Proprietary (Enterprise)

Features that enterprises need but individuals don't:

| Feature | Why It's Paid | Target |
|---|---|---|
| Dashboard authentication | Security teams require it; individuals don't care | Team+ |
| Row/column access policies | Multi-tenant data isolation | Business+ |
| PII masking/redaction | Compliance (GDPR, HIPAA) | Business+ |
| Query allow/deny lists | Maximum-safety environments | Business+ |
| Compliance audit exports | SOC2, HIPAA audit evidence | Business+ |
| SSO/SAML/OIDC | Enterprise identity integration | Enterprise |
| Multi-upstream database routing | Complex architectures | Enterprise |
| Connection pooling | Scale beyond 50 concurrent agents | Business+ |
| High-availability / failover | Production uptime guarantees | Enterprise |
| Centralized multi-instance management | Multi-region deployments | Enterprise |
| Prometheus / OpenTelemetry metrics | Observability stack integration | Team+ |
| Priority support | Faster response times | Business+ |

### Managed Service: GateSQL Cloud

The primary revenue driver long-term. A hosted proxy that sits between agents and customer databases.

**How it works:**
1. Customer signs up at gatesql.dev
2. Provides upstream database connection details (encrypted, stored securely)
3. Gets a GateSQL Cloud proxy endpoint: `proxy-abc123.gatesql.cloud:15432`
4. Creates sessions via the hosted admin API
5. Agents connect to the cloud proxy, which forwards to the customer's database

**Advantages over self-hosted:**
- Zero ops — no Docker, no config, no upgrades
- Managed TLS certificates
- Built-in monitoring and alerting
- Multi-region proxy endpoints (lower latency)
- Automatic scaling

---

## Pricing

### Value Metric: Agent Sessions per Month

Sessions are the right billing unit because:
- They directly correlate with value (each session = one AI agent safely accessing production data)
- They naturally grow as agent usage scales
- They're easy to understand and predict
- Agents, not humans, are the users — seat-based pricing doesn't make sense

### Tiers

#### Community — Free (Self-Hosted)

**Target:** Solo developers, evaluation, small projects

**Includes:**
- Full proxy functionality (everything in the open-source repo)
- Unlimited sessions (self-hosted)
- Single upstream database
- Basic dashboard (no auth)
- Community support (GitHub Issues)

**Purpose:** Drive adoption, build community, create upgrade pipeline.

#### Team — $149/month

**Target:** Startups, small teams, 5-50 agents

**Includes everything in Community, plus:**
- GateSQL Cloud (hosted proxy) — 500 sessions/month (+$0.20/session overage)
- Up to 3 upstream databases
- Dashboard authentication (username/password)
- Webhook notifications
- Table/schema allow/deny lists
- Log export (JSON, CSV)
- Prometheus metrics endpoint
- Email support (48h response)

**Why $149:** Credit-card purchasable without procurement. Comparable to Datadog small-team plans. A startup running 30 agents at $149/mo is paying less than the cost of one engineer-hour per month to build this themselves.

#### Business — $499/month

**Target:** Growth companies, compliance requirements, 50-500 agents

**Includes everything in Team, plus:**
- 5,000 sessions/month (+$0.08/session overage)
- Up to 10 upstream databases
- SSO/SAML integration
- Role-based access control for dashboard
- SOC2-ready audit logs (structured, exportable, tamper-evident)
- PII detection and masking
- High-availability proxy (active-passive failover)
- Policy-as-code (define policies in config, version in git)
- Connection pooling
- Priority support (4h response)

**Why $499:** The compliance features (SOC2 logs, SSO, PII detection) make this a "must-have" once a company starts their SOC2 journey. SOC2 audits cost $30-100K — this tool satisfies 2-3 controls for $6K/year.

#### Enterprise — Custom ($20K+/year)

**Target:** F500, regulated industries, 500+ agents

**Includes everything in Business, plus:**
- Unlimited sessions
- Unlimited upstream databases
- Secret manager integration (HashiCorp Vault, AWS Secrets Manager)
- Multi-region proxy deployment
- OPA (Open Policy Agent) policy integration
- Custom query governance rules
- Dedicated support engineer (1h P1 SLA)
- Quarterly business reviews
- Custom SLA with uptime guarantees
- BYOC (Bring Your Own Cloud) deployment option

**Why $20K+/year floor:** The cost of a single AI agent data breach is $4.45M (IBM 2024 Cost of a Data Breach report). GateSQL at $20K/year is 0.4% of that risk. Enterprise pricing is based on value delivered (risk reduction), not cost to serve.

---

## Unit Economics

### Managed Service COGS (Per Customer/Month)

| Component | Team ($149) | Business ($499) | Enterprise ($20K+/yr) |
|---|---|---|---|
| Compute (proxy) | $15 | $30 | $40-100 |
| Network egress | $2 | $10 | $20-50 |
| Storage (logs) | $5 | $15 | $30 |
| TLS certificates | $1 | $1 | $1 |
| Support | $10 | $30 | $50-200 |
| Infrastructure overhead | $4 | $14 | $30 |
| **Total COGS** | **$37** | **$100** | **$171-411** |
| **Gross margin** | **75%** | **80%** | **75-90%** |

The proxy is CPU-light and memory-light — it mostly forwards bytes with minimal processing per message. Excellent margins at every tier.

---

## Revenue Milestones

### $0 → $10K MRR (Months 0-12)

**What drives it:**
- Open-source adoption creates awareness and usage
- First GateSQL Cloud customers (self-serve, credit card)
- LangChain and CrewAI integration guides bring agent builders
- Supabase/Neon integration creates partnership traffic
- HN/Reddit/Product Hunt launches drive initial signups

**Key metrics to track:**
- GitHub stars (proxy for awareness)
- Docker pulls (proxy for trial)
- Active sessions per week (proxy for real usage)
- Cloud signups and activation rate

**Team:** 1-2 people (founder + maybe one engineer)

### $10K → $100K MRR (Months 12-24)

**What drives it:**
- Enterprise features ship (SSO, SOC2 logs, PII masking)
- First $5K+/month enterprise deals (3-5 accounts)
- AWS/GCP Marketplace listing (enterprises buy through marketplace credits)
- SOC2 Type II certification completed
- Case studies from design partners enable enterprise conversations

**Key metrics:**
- Enterprise pipeline (deals in progress)
- Net revenue retention (target: 130%+)
- Time to close (enterprise deal cycle)

**Team:** 3-5 people (2-3 engineers, 1 technical AE, 1 DevRel)

### $100K → $1M MRR (Months 24-48)

**What drives it:**
- Multi-database support (more databases per customer = natural expansion)
- Platform team adoption (entire organizations standardize on GateSQL)
- Land-and-expand in enterprises (one team → department → company)
- International expansion
- Additional database support beyond PostgreSQL (MySQL, etc.)

**Key metrics:**
- Logo count and average contract value
- Expansion revenue and NDR
- Support ticket volume and resolution time

**Team:** 10-15 people

---

## Fundraising

### Is This VC-Backable?

**Yes.** The TAM story: ~300K companies will deploy AI agents that need database access by 2028. Each needs governed access. "Picks and shovels" for AI agents.

### Comparable Companies

| Company | What | Raised | Outcome |
|---|---|---|---|
| Teleport | Secure access proxy (SSH, K8s, databases) | $110M | $1.1B valuation |
| Infisical | Open-source secrets management | $13M | Growing |
| Kong | API gateway | $271M | $1.4B valuation |
| HashiCorp | Infrastructure automation | $354M | Acquired by IBM for $6.4B |
| CockroachDB | Distributed SQL | $633M | $5B+ valuation |
| Neon | Serverless PostgreSQL | $104M | Growing |

GateSQL sits at the intersection of three hot spaces: AI infrastructure, database tooling, and security/governance. Each space has produced billion-dollar outcomes.

### Recommended Raise

**Seed: $1.5-3M** when you hit $10-20K MRR with strong open-source traction (1,000+ GitHub stars, 10K+ Docker pulls, active community).

**Target funds:**
- **OSS Capital** — Specifically invests in open-source companies. Led by Joseph Jacks.
- **Heavybit** — Developer tools focus. Portfolio includes Snyk, PlanetScale.
- **Amplify Partners** — Early-stage enterprise/infrastructure. Portfolio includes Teleport.
- **Boldstart Ventures** — First check in enterprise. Portfolio includes Snyk, BigPanda.

**Pitch structure:**
1. The problem: AI agents need database access. Current solutions are insecure (raw creds) or limiting (REST APIs).
2. The insight: SQL is the best tool-use interface. The problem is governance, not query language.
3. The product: Wire protocol proxy that adds session auth, budgets, purpose tracking, audit.
4. Traction: Stars, downloads, cloud customers, NRR.
5. Market: 300K companies × $6K-$50K/year = $1.8B-$15B TAM.
6. Team and moat: Deep protocol expertise, BSL prevents cloud commoditization.

### Alternative: Bootstrap

GateSQL can also be a highly profitable bootstrapped business:
- $1M ARR with 85% margins = $850K profit
- 2-3 person team
- No dilution, full control
- Sustainable and stress-free

This is a legitimate and potentially better path, depending on the founder's goals. The managed service margins are excellent and the product doesn't require a massive team.

---

## Design Partner Program

### Structure

Recruit 3-5 companies to be design partners. They get:
- Free Business-tier access for 12 months
- Direct Slack channel with the founder
- Bi-weekly 30-minute feedback calls
- Input on feature prioritization

In exchange, they provide:
- Bi-weekly feedback on their usage
- A published case study (with their approval)
- Logo rights for the website
- Reference availability for sales conversations

### Conversion

At month 13, offer a discounted Enterprise contract (30% off first year). The sunk cost of integration + switching cost + relationship makes conversion likely.

### Where to Find Design Partners

- HN commenters who express interest
- GitHub users who star the repo and have agent-related repos
- LangChain/CrewAI community members building data-heavy agents
- Companies that already use Supabase or Neon (warm intro through partnership)

---

## Expansion Revenue

### Target: 130-150% Net Revenue Retention

A single account naturally grows over time:

| Month | Tier | Sessions/mo | Databases | Annual spend |
|---|---|---|---|---|
| 1 | Team | 100 | 1 | $1,788 |
| 6 | Team | 400 | 2 | $1,788 |
| 12 | Business | 2,000 | 4 | $5,988 |
| 18 | Business | 4,500 | 7 | $5,988 |
| 24 | Enterprise | 25,000 | 15+ | $20,000+ |

**Growth drivers:**
- More agents → more sessions → higher tier
- More databases connected → need multi-DB support
- Compliance audit → need SOC2 logs, SSO, PII masking
- More teams using agents → company-wide standardization

### Upgrade Triggers (In-Product)

Surface upgrade prompts at natural friction points:
- "You've used 450/500 sessions this month. Upgrade to Business for 5,000 sessions →"
- "Dashboard auth requires Team tier. Upgrade to secure your dashboard →"
- "SSO integration requires Business tier. Upgrade for SAML/OIDC →"
- "You're connecting your 4th database. Business tier supports up to 10 →"

---

## Sales Motion

### Phase 1: Product-Led Growth (Now → $500K ARR)

- Self-serve signup at gatesql.dev
- Stripe billing (monthly, cancel anytime)
- In-product upgrade triggers
- Usage-based expansion
- No salespeople

### Phase 2: Sales-Assisted ($300K → $2M ARR)

- Hire first technical AE ($120K base + $120K OTE)
- Inbound leads from product-qualified signals (high usage, multiple databases, SSO inquiries)
- 30-day free Business trial for qualified accounts
- Demo calls focused on compliance and governance

### Phase 3: Enterprise Sales ($2M+ ARR)

- Hire VP Sales
- Named enterprise accounts
- SOC2 Type II certification enables procurement
- Annual contracts with multi-year discounts
- Customer success manager for top accounts

---

## Key Decisions Summary

| Decision | Recommendation | Confidence |
|---|---|---|
| License | BSL 1.1 | High — proven model for infra tools |
| Business model | Open-core + managed service | High — best revenue path |
| Value metric | Sessions/month | High — correlates with value |
| Team tier price | $149/mo | Medium — test and adjust |
| Business tier price | $499/mo | Medium — test and adjust |
| Enterprise floor | $20K/yr | Medium — depends on feature set |
| Fundraise vs bootstrap | Decide at $10K MRR | N/A — both paths viable |
| First hire | Engineer, then AE | High — product first |
| Name | GateSQL | High — strong, memorable |

---

## Immediate Next Steps

1. **Add BSL 1.1 license** to the repository
2. **Dashboard authentication** — the minimum viable paid feature
3. **Usage metering** — track sessions/month per API key to support future billing
4. **Recruit 3 design partners** before paid tiers ship
5. **Register gatesql.com or gatesql.dev**
6. **Write the category-defining blog post:** "Why AI Agents Need Database Governance"
