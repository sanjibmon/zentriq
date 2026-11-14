set -e
pkill -f 'zentriq-agent --run' || true; zentriq-agent --uninstall K6QD-RZVC-9DHA-QRXB 2>&1 || true; test -e /opt/zentriq-agent/zentriq-agent && echo STILL || echo GONE