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
    su -c "$PGBIN/psql -q -d demo -f /app/demo-store.sql" postgres
fi

echo ""
echo "========================================="
echo "  GateSQL Demo"
echo "========================================="
echo "  Proxy:     localhost:15432"
echo "  Dashboard: http://localhost:8080"
echo "  API Key:   pk_demo_key"
echo ""
echo "  Quick start:"
echo "    # Create a session"
echo "    curl -s -X POST http://localhost:8080/api/sessions \\"
echo "      -H 'Content-Type: application/json' \\"
echo "      -H 'X-Api-Key: pk_demo_key' \\"
echo "      -d '{\"agentId\":\"demo-agent\",\"task\":\"explore demo data\"}'"
echo ""
echo "    # Connect (use token from response as password)"
echo "    PGPASSWORD=<token> psql -h localhost -p 15432 -U agent -d demo"
echo "========================================="
echo ""

# Start GateSQL proxy (foreground)
exec dotnet DbProxy.dll /app/config.json
