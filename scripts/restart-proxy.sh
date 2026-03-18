#!/bin/bash
lsof -ti:15432 -ti:8080 | xargs -r kill -9 2>/dev/null
sleep 1
nohup dotnet run --project src/DbProxy -- /home/martin/source/db-proxy/config.json > /tmp/dbproxy.log 2>&1 &
echo "PID: $!"
sleep 3
tail -5 /tmp/dbproxy.log
