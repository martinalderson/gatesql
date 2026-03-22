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

# Start GateSQL proxy (foreground)
exec dotnet DbProxy.dll /app/config.json
