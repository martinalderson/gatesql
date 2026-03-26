#!/bin/bash
set -e

PGBIN="/usr/lib/postgresql/$(ls /usr/lib/postgresql/)/bin"
PGDATA="/var/lib/postgresql/data"

# ── Phase 1: PostgreSQL ──
if [ ! -f "$PGDATA/PG_VERSION" ]; then
    su -c "$PGBIN/initdb -D $PGDATA --auth-local=trust --auth-host=trust" postgres > /dev/null
    echo "host all all 127.0.0.1/32 trust" >> "$PGDATA/pg_hba.conf"
fi

su -c "$PGBIN/pg_ctl start -D $PGDATA -l /var/log/postgresql.log -o '-c listen_addresses=localhost'" postgres > /dev/null

until su -c "$PGBIN/pg_isready -q" postgres; do
    sleep 0.5
done

# ── Phase 2: Seed demo database ──
if ! su -c "$PGBIN/psql -lqt" postgres | grep -qw demo; then
    su -c "$PGBIN/createdb demo" postgres
    su -c "$PGBIN/psql -q -d demo -f /app/demo-store.sql" postgres > /dev/null
fi

# ── Phase 3: Start GateSQL proxy ──
dotnet /app/DbProxy.dll /app/config.json > /dev/null 2>&1 &
DOTNET_PID=$!

echo "Starting GateSQL..."
for i in $(seq 1 30); do
    if curl -sf http://localhost:8080/api/sessions -H "X-Api-Key: pk_demo_key" > /dev/null 2>&1; then
        break
    fi
    if [ "$i" -eq 30 ]; then
        echo "Error: GateSQL proxy failed to start" >&2
        exit 1
    fi
    sleep 1
done

# ── Phase 4: Create session ──
RESPONSE=$(curl -s -X POST http://localhost:8080/api/sessions \
    -H "Content-Type: application/json" \
    -H "X-Api-Key: pk_demo_key" \
    -d '{"agentId":"opencode","task":"interactive demo","queryBudget":500}')

CONNECTION_STRING=$(echo "$RESPONSE" | jq -r '.connectionString')
if [ -z "$CONNECTION_STRING" ] || [ "$CONNECTION_STRING" = "null" ]; then
    echo "Error: Failed to create GateSQL session" >&2
    echo "$RESPONSE" >&2
    exit 1
fi

# ── Phase 5: Configure OpenCode ──
if [ -n "$AUTH_JSON_BASE64" ]; then
    mkdir -p ~/.local/share/opencode
    echo "$AUTH_JSON_BASE64" | base64 -d > ~/.local/share/opencode/auth.json
fi

cat > /workspace/opencode.json << 'CONF'
{
  "$schema": "https://opencode.ai/config.json",
  "permission": {
    "*": "allow"
  }
}
CONF

cat > /workspace/OPENCODE.md << EOF
# GateSQL Demo Database

You have access to an ecommerce demo database through GateSQL (a secure PostgreSQL proxy for AI agents).

## Connection

Use psql to query the database:

\`\`\`bash
psql "$CONNECTION_STRING"
\`\`\`

## Database Schema

The database contains an ecommerce store with these tables:
- **customers** - customer profiles (name, email, city, country, tier)
- **categories** - product categories (hierarchical)
- **products** - product catalog (sku, name, price, cost, weight)
- **inventory** - stock levels per product and warehouse
- **coupons** - discount codes
- **orders** - customer orders with status tracking
- **order_items** - line items per order
- **reviews** - product reviews with ratings
- **page_views** - browse activity

## Important: Purpose Comments

All queries MUST include a purpose comment or they will be rejected. Format:

\`\`\`sql
SELECT /* <agent_purpose>finding top customers by revenue</agent_purpose> */
  c.name, SUM(o.total) as revenue
FROM customers c
JOIN orders o ON o.customer_id = c.id
GROUP BY c.name
ORDER BY revenue DESC
LIMIT 10;
\`\`\`

The comment \`/* <agent_purpose>your reason here</agent_purpose> */\` must appear in every query.

Session commands (SET, BEGIN, COMMIT, ROLLBACK) and schema introspection (\dt, \d tablename) are exempt.

## Tips

- Explore the schema: \`psql "$CONNECTION_STRING" -c "\dt"\`
- Describe a table: \`psql "$CONNECTION_STRING" -c "\d customers"\`
- The session has a budget of 500 queries
- The GateSQL dashboard at http://localhost:8080 logs all queries
EOF

export DATABASE_URL="$CONNECTION_STRING"

# ── Phase 6: Run OpenCode TUI ──
if [ ! -t 0 ]; then
    echo "Error: OpenCode requires an interactive terminal." >&2
    echo "Run with: docker run -it --rm -p 8080:8080 gatesql/gatesql:demo-opencode" >&2
    exit 1
fi

cleanup() {
    kill "$DOTNET_PID" 2>/dev/null
    wait "$DOTNET_PID" 2>/dev/null
    su -c "$PGBIN/pg_ctl stop -D $PGDATA -m fast" postgres 2>/dev/null
}
trap cleanup SIGTERM SIGINT EXIT

echo ""
echo "GateSQL demo ready — dashboard at http://localhost:8080"
echo ""

MODEL_FLAG=""
if [ -n "$MODEL" ]; then
    MODEL_FLAG="-m $MODEL"
fi

opencode $MODEL_FLAG
