#!/bin/sh
# Zentriq Agent for Linux (installed from .tar.gz): removal. Use "sudo zentriq-agent --uninstall <code>" while tamper
# protection is on (the code comes from the Zentriq console).
set -eu
[ "$(id -u)" = "0" ] || { echo "Run as root." >&2; exit 1; }
/opt/zentriq-agent/zentriq-agent --check-uninstall
/opt/zentriq-agent/zentriq-agent --cleanup-hooks >/dev/null 2>&1 || true
rm -rf /var/spool/zentriq-agent
if [ -d /run/systemd/system ]; then
  rm -f /etc/systemd/system/zentriq-agent.service.d/10-protection.conf
  systemctl daemon-reload >/dev/null 2>&1 || true
  systemctl disable --now zentriq-agent-watchdog.timer zentriq-agent.service >/dev/null 2>&1 || true
  rm -f /etc/systemd/system/zentriq-agent.service /etc/systemd/system/zentriq-agent-watchdog.service /etc/systemd/system/zentriq-agent-watchdog.timer
  rm -rf /etc/systemd/system/zentriq-agent.service.d
  systemctl daemon-reload >/dev/null 2>&1 || true
fi
rm -f /usr/bin/zentriq-agent
rm -rf /opt/zentriq-agent /var/lib/zentriq-agent /var/log/zentriq-agent
echo "The Zentriq Agent was removed."
