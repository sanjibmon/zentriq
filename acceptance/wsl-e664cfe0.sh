set -e
pkill -f 'zentriq-agent --run' || true; zentriq-agent --uninstall EKGP-J4CQ-Y577-HYFN 2>&1 || true; test -e /opt/zentriq-agent/zentriq-agent && echo STILL || echo GONE