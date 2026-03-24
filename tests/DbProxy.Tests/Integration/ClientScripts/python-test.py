import os
import psycopg

host = os.environ["PGHOST"]
port = os.environ["PGPORT"]
password = os.environ["PGPASSWORD"]

conninfo = f"host={host} port={port} user=agent password={password} dbname=postgres"

conn = psycopg.connect(conninfo)
conn.autocommit = True

# Query with purpose
print("=== psycopg: query with purpose ===")
cur = conn.execute("/* <agent_purpose>python e2e test</agent_purpose> */ SELECT 42 AS result")
row = cur.fetchone()
assert row[0] == 42, f"expected 42, got {row[0]}"
print(f"OK: got {row[0]}")

# Query without purpose (should fail)
print("=== psycopg: query without purpose (should fail) ===")
try:
    conn.execute("SELECT 1")
    print("FAIL: query without purpose was not rejected")
    raise SystemExit(1)
except psycopg.errors.Error as e:
    if "agent_purpose" in str(e):
        print("OK: rejected without purpose")
    else:
        print(f"FAIL: unexpected error: {e}")
        raise SystemExit(1)

conn.close()
print("=== psycopg: all tests passed ===")
