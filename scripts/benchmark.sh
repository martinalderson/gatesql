#!/bin/bash
# Create a session and run pgbench comparison
TOKEN=$(curl -s -X POST http://localhost:8080/api/sessions \
  -H "Content-Type: application/json" -H "X-Api-Key: pk_dev_123" \
  -d '{"agentId":"pgbench","task":"benchmark","queryBudget":100000}' | python3 -c "import sys,json; print(json.load(sys.stdin)['token'])")

echo "=== Read-Write via PROXY: 10 clients, 5 seconds ==="
PGPASSWORD="$TOKEN" pgbench -h 127.0.0.1 -p 15432 -U agent -n -f bench/tpcb.sql -c 10 -T 5 -P 1 postgres

echo ""
echo "=== Read-Write DIRECT: 10 clients, 5 seconds ==="
PGPASSWORD=postgres pgbench -h 127.0.0.1 -p 5432 -U postgres -n -c 10 -T 5 -P 1 postgres
