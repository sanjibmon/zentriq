#!/bin/sh
# Zentriq Agent for Linux: installs from this unpacked .tar.gz (for distributions without dpkg or rpm, or where a
# package cannot be used). Run as root:  sudo ./install.sh [--enroll <code> --server https://<server>:8444]
#   --upgrade   used by the agent's self-update (keeps enrollment and settings)
set -eu
here=$(cd "$(dirname "$0")" && pwd)
[ "$(id -u)" = "0" ] || { echo "Run as root (sudo ./install.sh)." >&2; exit 1; }
[ -d /run/systemd/system ] || echo "Note: systemd is not running; the agent is installed but not started as a service." >&2
upgrade=0; enroll=""; server=""; extra=""
while [ $# -gt 0 ]; do
  case "$1" in
    --upgrade) upgrade=1 ;;
    --enroll) enroll="$2"; shift ;;
    --server) server="$2"; shift ;;
    --checkin|--inventory|--proxy) extra="$extra $1 $2"; shift ;;
  esac
  shift
done
if [ -d /run/systemd/system ]; then
  rm -f /etc/systemd/system/zentriq-agent.service.d/10-protection.conf
  systemctl daemon-reload >/dev/null 2>&1 || true
  systemctl stop zentriq-agent.service >/dev/null 2>&1 || true
fi
mkdir -p /opt/zentriq-agent /var/lib/zentriq-agent /var/log/zentriq-agent
cp -a "$here"/bin/. /opt/zentriq-agent/
cp "$here"/uninstall.sh /opt/zentriq-agent/uninstall.sh
chmod 755 /opt/zentriq-agent/zentriq-agent /opt/zentriq-agent/uninstall.sh
echo tar > /opt/zentriq-agent/installed-by
chmod 700 /var/lib/zentriq-agent; chmod 750 /var/log/zentriq-agent
ln -sf /opt/zentriq-agent/zentriq-agent /usr/bin/zentriq-agent
if [ -d /run/systemd/system ]; then
  cp "$here"/systemd/*.service "$here"/systemd/*.timer /etc/systemd/system/
  systemctl daemon-reload
  systemctl enable zentriq-agent.service zentriq-agent-watchdog.timer >/dev/null 2>&1
  systemctl restart zentriq-agent.service
  systemctl start zentriq-agent-watchdog.timer >/dev/null 2>&1 || true
fi
echo "Zentriq Agent $(/opt/zentriq-agent/zentriq-agent --version) installed."
if [ "$upgrade" = "0" ] && [ -n "$enroll" ]; then
  # shellcheck disable=SC2086
  /opt/zentriq-agent/zentriq-agent --enroll "$enroll" ${server:+--server "$server"} $extra
fi
