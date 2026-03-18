\set aid random(1, 100000)
/* <agent_purpose>pgbench read test</agent_purpose> */ SELECT abalance FROM pgbench_accounts WHERE aid = :aid;
