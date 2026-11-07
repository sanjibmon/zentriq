set -e
pkill -f 'zentriq-agent --run' || true; zentriq-agent --uninstall E7DA-WHW6-963D-Y7YZ 2>&1 || true; test -e /opt/zentriq-agent/zentriq-agent && echo STILL || echo GONE