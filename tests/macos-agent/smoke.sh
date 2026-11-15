#!/bin/bash
# Zentriq Agent for macOS: install the package on this Mac (GitHub-hosted runner), check the daemon, the version, the
# inventory it collects, the CLI, and removal. Writes a Markdown result table.
# Usage: tests/macos-agent/smoke.sh <package dir> <result.md>
set -uo pipefail
dir=${1:?package dir}; md=${2:?result file}
version=$(cat "$dir/version")
arch=$([ "$(uname -m)" = arm64 ] && echo arm64 || echo x64)
pkg="$dir/zentriq-agent-macos-$arch.pkg"
pass=0; fail=0
row() { if [ "$2" = PASS ]; then pass=$((pass+1)); else fail=$((fail+1)); fi; echo "| $1 | **$2** | $3 |" >> "$md"; echo "$2 $1: $3"; }
echo "| Check | Result | Detail |" > "$md"; echo "|---|---|---|" >> "$md"

sudo installer -pkg "$pkg" -target / >/tmp/zq-install.log 2>&1 && row "M01 install the package" PASS "$(sw_vers -productName) $(sw_vers -productVersion), $(uname -m)" || row "M01 install the package" FAIL "$(tail -3 /tmp/zq-install.log)"
sleep 5
if sudo launchctl print system/com.zentriq.agent >/tmp/zq-launchd.txt 2>&1 && grep -q "state = running" /tmp/zq-launchd.txt; then row "M02 launchd daemon running" PASS "com.zentriq.agent"; else row "M02 launchd daemon running" FAIL "$(grep -m3 'state\|last exit' /tmp/zq-launchd.txt | tr '\n' ' ')"; fi
v=$(/usr/local/bin/zentriq-agent --version 2>&1)
[ "$v" = "$version" ] && row "M03 version" PASS "$v" || row "M03 version" FAIL "got '$v', expected $version"
codesign --verify /Library/Zentriq/Agent/zentriq-agent 2>/tmp/zq-cs.txt && row "M04 signature" PASS "$(codesign -dv /Library/Zentriq/Agent/zentriq-agent 2>&1 | grep -m1 Signature)" || row "M04 signature" FAIL "$(cat /tmp/zq-cs.txt)"
start=$(date +%s)
sudo /usr/local/bin/zentriq-agent --collect /tmp/zq-inv.json >/tmp/zq-collect.txt 2>&1
took=$(( $(date +%s) - start ))
res=$(python3 - <<'P'
import json
d=json.load(open('/tmp/zq-inv.json'))
os_=d.get('os',{}); dev=d.get('device',{}); hw=d.get('hardware',{}); sec=d.get('security',{})
sw=d.get('software',[]); withver=[s for s in sw if s.get('version')]
problems=[]
if os_.get('family')!='macos': problems.append('os family '+str(os_.get('family')))
if not dev.get('serialNumber'): problems.append('no serial')
if not dev.get('smbiosUuid'): problems.append('no hardware UUID')
if not hw.get('memoryBytes'): problems.append('no memory')
if len(sw)<5: problems.append(f'{len(sw)} apps')
if len(withver)<len(sw)//2: problems.append(f'only {len(withver)} of {len(sw)} apps with a version')
print(('OK ' if not problems else 'BAD ')+f"{os_.get('name')}, {dev.get('manufacturer')} {dev.get('model')}, {hw.get('cpuName')}, {round((hw.get('memoryBytes') or 0)/2**30)} GB, {len(sw)} apps ({len(withver)} with version), FileVault {sec.get('diskEncryption')}, firewall {sec.get('firewallEnabled')}, SIP {sec.get('secureBoot')}, admins {d.get('users',{}).get('localAdmins')}, errors {d.get('errors')}" + ('' if not problems else ' - ' + ', '.join(problems)))
P
)
case "$res" in OK*) row "M05 inventory" PASS "${res#OK } (${took}s)";; *) row "M05 inventory" FAIL "${res#BAD } $(tail -2 /tmp/zq-collect.txt)";; esac
sudo /usr/local/bin/zentriq-agent --status >/tmp/zq-status.txt 2>&1
grep -q "macos-$arch" /tmp/zq-status.txt && grep -q "installed as pkg" /tmp/zq-status.txt && row "M06 status" PASS "$(head -2 /tmp/zq-status.txt | tr '\n' ' ')" || row "M06 status" FAIL "$(cat /tmp/zq-status.txt | tr '\n' ' ')"
out=$(sudo /usr/local/bin/zentriq-agent --enroll ABCDE-FGHJK-MNPQR-STUVW --server https://127.0.0.1:9 2>&1); rc=$?
[ $rc -ne 0 ] && row "M07 enrollment against an unreachable server fails cleanly" PASS "$(echo "$out" | tail -1)" || row "M07 enrollment against an unreachable server fails cleanly" FAIL "exit $rc: $out"
sleep 2
sudo tail -5 "/Library/Logs/Zentriq Agent/agent.log" > /tmp/zq-log.txt 2>&1
grep -q "started (macos-" "/Library/Logs/Zentriq Agent/agent.log" 2>/dev/null && row "M08 service log" PASS "$(grep -m1 'started (macos-' '/Library/Logs/Zentriq Agent/agent.log' | cut -c1-120)" || row "M08 service log" FAIL "$(cat /tmp/zq-log.txt | tr '\n' ' ')"
sudo installer -pkg "$pkg" -target / >/dev/null 2>&1 && sleep 5 && sudo launchctl print system/com.zentriq.agent 2>/dev/null | grep -q "state = running" && row "M09 reinstall (upgrade path) keeps the daemon running" PASS "" || row "M09 reinstall (upgrade path) keeps the daemon running" FAIL ""
sudo /Library/Zentriq/Agent/uninstall.sh >/tmp/zq-un.txt 2>&1
[ ! -e /Library/Zentriq/Agent ] && ! sudo launchctl print system/com.zentriq.agent >/dev/null 2>&1 && row "M10 uninstall" PASS "$(tail -1 /tmp/zq-un.txt)" || row "M10 uninstall" FAIL "$(cat /tmp/zq-un.txt | tr '\n' ' ')"
echo "" >> "$md"; echo "**$pass passed, $fail failed**" >> "$md"
echo "$pass passed, $fail failed"
[ $fail -eq 0 ]
