const { Client } = require('pg');

async function main() {
  const client = new Client({
    host: process.env.PGHOST,
    port: parseInt(process.env.PGPORT),
    user: 'agent',
    password: process.env.PGPASSWORD,
    database: 'postgres',
  });

  await client.connect();

  // Query with purpose
  console.log('=== node-postgres: query with purpose ===');
  const res = await client.query("/* <agent_purpose>node e2e test</agent_purpose> */ SELECT 42 AS result");
  if (res.rows[0].result !== 42) {
    console.error(`FAIL: expected 42, got ${res.rows[0].result}`);
    process.exit(1);
  }
  console.log('OK: got', res.rows[0].result);

  // Query without purpose (should fail)
  console.log('=== node-postgres: query without purpose (should fail) ===');
  try {
    await client.query("SELECT 1");
    console.error('FAIL: query without purpose was not rejected');
    process.exit(1);
  } catch (err) {
    if (err.message.includes('agent_purpose')) {
      console.log('OK: rejected without purpose');
    } else {
      console.error('FAIL: unexpected error:', err.message);
      process.exit(1);
    }
  }

  await client.end();
  console.log('=== node-postgres: all tests passed ===');
}

main().catch(err => { console.error(err); process.exit(1); });
