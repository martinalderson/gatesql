#!/bin/bash
set -e

PGBIN="/usr/lib/postgresql/$(ls /usr/lib/postgresql/)/bin"
PGDATA="/var/lib/postgresql/data"

# Initialize PostgreSQL if needed
if [ ! -f "$PGDATA/PG_VERSION" ]; then
    su -c "$PGBIN/initdb -D $PGDATA --auth-local=trust --auth-host=trust" postgres > /dev/null
    echo "host all all 127.0.0.1/32 trust" >> "$PGDATA/pg_hba.conf"
fi

# Start PostgreSQL
su -c "$PGBIN/pg_ctl start -D $PGDATA -l /var/log/postgresql.log -o '-c listen_addresses=localhost'" postgres > /dev/null

# Wait for PostgreSQL to be ready
until su -c "$PGBIN/pg_isready -q" postgres; do
    sleep 0.5
done

# Seed demo database (idempotent)
if ! su -c "$PGBIN/psql -lqt" postgres | grep -qw demo; then
    su -c "$PGBIN/createdb demo" postgres
    su -c "$PGBIN/psql -q -d demo -f /app/demo-store.sql" postgres > /dev/null
fi

# Trap signals for clean shutdown of both processes
cleanup() {
    kill "$DOTNET_PID" 2>/dev/null
    wait "$DOTNET_PID" 2>/dev/null
    su -c "$PGBIN/pg_ctl stop -D $PGDATA -m fast" postgres 2>/dev/null
}
trap cleanup SIGTERM SIGINT EXIT

# Start GateSQL proxy (background, then wait)
dotnet DbProxy.dll /app/config.json &
DOTNET_PID=$!

# Wait for GateSQL to be ready
for i in $(seq 1 30); do
    if curl -sf http://localhost:8080/api/sessions -H "X-Api-Key: pk_demo_key" > /dev/null 2>&1; then
        break
    fi
    if [ "$i" -eq 30 ]; then
        wait "$DOTNET_PID"
        exit 1
    fi
    sleep 1
done

# Create a demo MCP session
MCP_RESPONSE=$(curl -s -X POST http://localhost:8080/api/sessions \
    -H "Content-Type: application/json" \
    -H "X-Api-Key: pk_demo_key" \
    -d '{"agentId":"mcp-demo","task":"demo session for MCP clients","queryBudget":1000,"readOnly":true}')

MCP_TOKEN=$(echo "$MCP_RESPONSE" | jq -r '.token // empty')

if [ -n "$MCP_TOKEN" ]; then
    echo ""
    echo "  ── MCP Server ──────────────────────────────────────"
    echo ""
    echo "  Endpoint:  http://localhost:8080/mcp"
    echo "  Auth:      Bearer $MCP_TOKEN"
    echo ""
    echo "  Claude Desktop config (Settings > MCP Servers):"
    echo ""
    echo "    {"
    echo "      \"mcpServers\": {"
    echo "        \"gatesql-demo\": {"
    echo "          \"url\": \"http://localhost:8080/mcp\","
    echo "          \"headers\": {"
    echo "            \"Authorization\": \"Bearer $MCP_TOKEN\""
    echo "          }"
    echo "        }"
    echo "      }"
    echo "    }"
    echo ""
    echo "  Session: read-only, 1000 query budget"
    echo "  ─────────────────────────────────────────────────────"
    echo ""
fi

wait "$DOTNET_PID"
