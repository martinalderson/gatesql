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

echo ""
echo "Starting GateSQL with demo database..."
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
echo "Getting agent-scoped session from GateSQL..."
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

cat > /workspace/AGENTS.md << 'AGENTS_EOF'
# GateSQL Demo Database

You have access to an ecommerce demo database via psql.

## CRITICAL: Every SQL query MUST use this exact comment format or it will be rejected

```
/* <agent_purpose>your reason here</agent_purpose> */ SELECT ...
```

The `<agent_purpose>` XML tags inside the comment are mandatory. A plain comment like `/* reason */` will NOT work.

Correct:
```sql
psql -c "/* <agent_purpose>getting total revenue</agent_purpose> */ SELECT SUM(total) FROM orders;"
```

Wrong (will be rejected):
```sql
psql -c "/* getting total revenue */ SELECT SUM(total) FROM orders;"
psql -c "SELECT /* getting total revenue */ SUM(total) FROM orders;"
```

## Connection

psql is pre-configured — just run `psql` with no arguments, or use `psql -c "..."` for one-off queries.

## Tips

- Explore the schema: `psql -c "\dt"`
- Describe a table: `psql -c "\d customers"`
- The session has a budget of 500 queries
AGENTS_EOF

echo "Getting live schema for AGENTS.md..."
SCHEMA=$(curl -s http://localhost:8080/api/schema -H "X-Api-Key: pk_demo_key")
if [ -n "$SCHEMA" ] && ! echo "$SCHEMA" | jq -e '.error' > /dev/null 2>&1; then
    printf "\n## Database Schema\n\n%s\n" "$SCHEMA" >> /workspace/AGENTS.md
fi

echo "Configuring psql with scoped session..."
TOKEN=$(echo "$RESPONSE" | jq -r '.token')
export PGHOST=localhost
export PGPORT=15432
export PGDATABASE=demo
export PGUSER=agent
export PGPASSWORD="$TOKEN"

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
echo "Ready. Open your browser at http://localhost:8080 to view the dashboard."
echo "API key: pk_demo_key"
echo "MCP endpoint: http://localhost:8080/mcp (use Bearer token from session)"
echo ""
echo "Try asking: \"What can you tell me about our sales data?\""
echo ""
printf "\033[1mPress ENTER to launch OpenCode to query the database. Watch the queries live on the dashboard.\033[0m"
read -r
echo ""

MODEL_FLAG=""
if [ -n "$MODEL" ]; then
    MODEL_FLAG="-m $MODEL"
fi

opencode $MODEL_FLAG
