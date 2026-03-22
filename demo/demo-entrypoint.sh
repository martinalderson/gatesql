#!/bin/bash
set -e

# PostgreSQL binaries are installed under a versioned path
export PATH="/usr/lib/postgresql/$(ls /usr/lib/postgresql/)/bin:$PATH"

PGDATA="/var/lib/postgresql/data"

# Initialize PostgreSQL if needed
if [ ! -f "$PGDATA/PG_VERSION" ]; then
    echo "Initializing PostgreSQL..."
    su -c "initdb -D $PGDATA" postgres
    # Allow local connections without password
    echo "host all all 127.0.0.1/32 trust" >> "$PGDATA/pg_hba.conf"
fi

# Start PostgreSQL
echo "Starting PostgreSQL..."
su -c "pg_ctl start -D $PGDATA -l /var/log/postgresql.log -o '-c listen_addresses=localhost'" postgres

# Wait for PostgreSQL to be ready
echo "Waiting for PostgreSQL..."
until su -c "pg_isready -q" postgres; do
    sleep 0.5
done

# Seed demo database (idempotent)
if ! su -c "psql -lqt" postgres | grep -qw demo; then
    echo "Creating demo database..."
    su -c "createdb demo" postgres
    su -c "psql -d demo -f /app/demo-store.sql" postgres
    echo "Demo database seeded."
else
    echo "Demo database already exists, skipping seed."
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
