#!/bin/sh
# Zentriq Agent for macOS: removal. Use "sudo zentriq-agent --uninstall <code>" while tamper protection is on
# (the code comes from the Zentriq console).
set -eu
[ "$(id -u)" = "0" ] || { echo "Run as root (sudo)." >&2; exit 1; }
/Library/Zentriq/Agent/zentriq-agent --check-uninstall
/bin/launchctl bootout system/com.zentriq.agent >/dev/null 2>&1 || true
rm -f /Library/LaunchDaemons/com.zentriq.agent.plist /usr/local/bin/zentriq-agent
rm -rf /Library/Zentriq/Agent "/Library/Application Support/Zentriq/Agent"
rmdir /Library/Zentriq "/Library/Application Support/Zentriq" >/dev/null 2>&1 || true
/usr/sbin/pkgutil --forget com.zentriq.agent >/dev/null 2>&1 || true
echo "The Zentriq Agent was removed."
