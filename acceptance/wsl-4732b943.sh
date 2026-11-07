set -e
setsid nohup /opt/zentriq-agent/zentriq-agent --run >/var/log/zentriq-agent/run.out 2>&1 < /dev/null &
sleep 3