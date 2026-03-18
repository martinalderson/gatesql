\set aid random(1, 100000 * :scale)
\set bid random(1, 1 * :scale)
\set tid random(1, 10 * :scale)
\set delta random(-5000, 5000)
/* <agent_purpose>pgbench tpc-b</agent_purpose> */ UPDATE pgbench_accounts SET abalance = abalance + :delta WHERE aid = :aid;
/* <agent_purpose>pgbench tpc-b</agent_purpose> */ SELECT abalance FROM pgbench_accounts WHERE aid = :aid;
/* <agent_purpose>pgbench tpc-b</agent_purpose> */ UPDATE pgbench_tellers SET tbalance = tbalance + :delta WHERE tid = :tid;
/* <agent_purpose>pgbench tpc-b</agent_purpose> */ UPDATE pgbench_branches SET bbalance = bbalance + :delta WHERE bid = :bid;
/* <agent_purpose>pgbench tpc-b</agent_purpose> */ INSERT INTO pgbench_history (tid, bid, aid, delta, mtime) VALUES (:tid, :bid, :aid, :delta, CURRENT_TIMESTAMP);
