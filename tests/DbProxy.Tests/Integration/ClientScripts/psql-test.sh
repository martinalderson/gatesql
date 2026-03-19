#!/bin/bash
set -e

echo "=== psql: query with purpose ==="
RESULT=$(PGPASSWORD="$PGPASSWORD" psql -h "$PGHOST" -p "$PGPORT" -U agent -d postgres -tAc \
  "/* <agent_purpose>psql e2e test</agent_purpose> */ SELECT 42")
if [ "$RESULT" != "42" ]; then
  echo "FAIL: expected 42, got '$RESULT'"
  exit 1
fi
echo "OK: got $RESULT"

echo "=== psql: query without purpose (should fail) ==="
if PGPASSWORD="$PGPASSWORD" psql -h "$PGHOST" -p "$PGPORT" -U agent -d postgres -c "SELECT 1" 2>&1 | grep -q "agent_purpose"; then
  echo "OK: rejected without purpose"
else
  echo "FAIL: query without purpose was not rejected"
  exit 1
fi

echo "=== psql: all tests passed ==="
