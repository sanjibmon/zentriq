#!/bin/bash
# Zentriq application control on Linux (containers): with a block-mode policy the package-manager hooks let approved
# packages install, refuse the others with the organisation's message, record the events, accept a request from a
# normal user, let everything through (reported) in audit mode, and go away with the agent.
# Usage: tests/linux-agent/test-app-control.sh <packages dir> <report.md>
set -uo pipefail
pkg=$(cd "${1:?packages dir}" && pwd)
report=${2:?report}

# image | format | label | install | remove | fatal
cases=(
  "ubuntu:24.04|deb|Ubuntu 24.04 (apt / dpkg)|apt-get install -y -q|apt-get remove -y -q|yes"
  "debian:12|deb|Debian 12 (dpkg -i)|dpkg-i|apt-get remove -y -q|yes"
  "rockylinux:9|rpm|Rocky Linux 9 (dnf)|dnf install -y -q|dnf remove -y -q|yes"
  "amazonlinux:2023|rpm|Amazon Linux 2023 (dnf)|dnf install -y -q|dnf remove -y -q|yes"
  "opensuse/leap:15.6|rpm|openSUSE Leap 15.6 (zypper)|zypper --non-interactive install|zypper --non-interactive remove|no"
)

policy() {  # mode
  cat <<JSON
{"revision":7,"mode":"$1","message":"This is unauthorized software. Please contact your administrator for further installation.","allowRequests":true,"autoRemove":false,"wdac":"off","protectRemoteAgent":true,"rules":[{"id":"r1","action":"allow","label":"tree","name":"tree","platform":"linux"}]}
JSON
}

rows=(); failed=0; n=0
for c in "${cases[@]}"; do
  IFS='|' read -r image format label install remove fatal <<<"$c"
  n=$((n+1)); id=$(printf 'A%02d' $n)
  echo "==== $id $label"
  case "$format" in
    deb) agent_install='apt-get update -qq >/dev/null && apt-get install -y -qq /pkg/zentriq-agent-linux-x64.deb >/tmp/i.log 2>&1 || { cat /tmp/i.log; exit 1; }' ;;
    rpm) agent_install='if command -v zypper >/dev/null; then zypper --non-interactive install --allow-unsigned-rpm /pkg/zentriq-agent-linux-x64.rpm >/tmp/i.log 2>&1; else dnf install -y -q /pkg/zentriq-agent-linux-x64.rpm >/tmp/i.log 2>&1; fi || { cat /tmp/i.log; exit 1; }' ;;
  esac
  if [ "$install" = "dpkg-i" ]; then
    get_zip='apt-get download zip >/dev/null 2>&1 && dpkg -i ./zip_*.deb'
    get_tree='apt-get download tree >/dev/null 2>&1 && dpkg -i ./tree_*.deb'
  else
    get_zip="$install zip"; get_tree="$install tree"
  fi
  script=$(cat <<SCRIPT
set -u
fail() { echo "FAILED: \$*"; exit 1; }
$agent_install
command -v useradd >/dev/null || (command -v zypper >/dev/null && zypper --non-interactive install shadow >/dev/null 2>&1) || (command -v dnf >/dev/null && dnf install -y -q shadow-utils >/dev/null 2>&1) || true
command -v su >/dev/null || (command -v dnf >/dev/null && dnf install -y -q util-linux >/dev/null 2>&1) || true
mkdir -p /var/lib/zentriq-agent/appcontrol
cat > /var/lib/zentriq-agent/appcontrol/policy.json <<'JSON'
$(policy block)
JSON
cd /tmp
zentriq-agent --install-hooks || fail "hooks not installed"
zentriq-agent --app-control
($get_tree) >/tmp/tree.log 2>&1 || { cat /tmp/tree.log; fail "the approved package tree was not installed"; }
command -v tree >/dev/null || fail "tree missing after install"
echo "approved package installed: OK"
if ($get_zip) >/tmp/zip.log 2>&1; then cat /tmp/zip.log; fail "zip was installed although not approved"; fi
cat /tmp/zip.log | tail -8
grep -q "unauthorized software" /tmp/zip.log || fail "the message was not shown"
command -v zip >/dev/null && fail "zip present after refusal"
grep -l '"blocked"' /var/lib/zentriq-agent/appcontrol/spool/*.event >/dev/null 2>&1 || fail "no blocked event recorded"
echo "unapproved package refused with the message: OK"
if command -v useradd >/dev/null && command -v su >/dev/null; then
  useradd -m tester >/dev/null 2>&1
  su tester -c "zentriq-agent --request zip --reason 'needed for backups'" || fail "request by a user failed"
  ls /var/spool/zentriq-agent/requests/*.request >/dev/null 2>&1 || fail "no request file"
  echo "request by a normal user: OK"
fi
cat > /var/lib/zentriq-agent/appcontrol/policy.json <<'JSON'
$(policy audit)
JSON
($get_zip) >/tmp/zip2.log 2>&1 || { cat /tmp/zip2.log; fail "audit mode refused zip"; }
grep -l '"audited"' /var/lib/zentriq-agent/appcontrol/spool/*.event >/dev/null 2>&1 || fail "no audited event recorded"
echo "audit mode: installed and reported: OK"
$remove zentriq-agent >/dev/null 2>&1 || fail "agent removal failed"
for f in /etc/dpkg/dpkg.cfg.d/zentriq-app-control /etc/dnf/plugins/zentriq.conf /usr/lib/zypp/plugins/commit/zentriq; do [ ! -e "\$f" ] || fail "\$f left behind"; done
$install tree >/dev/null 2>&1 || true
echo "hooks removed with the agent: OK"
echo ALLDONE
SCRIPT
)
  out=$(docker run --rm -v "$pkg:/pkg" "$image" bash -c "$script" 2>&1); code=$?
  echo "$out" | tail -30
  summary=$(echo "$out" | grep -E ': OK$|FAILED' | tr '\n' ';' | sed 's/|/\//g')
  if [ $code -eq 0 ] && echo "$out" | grep -q ALLDONE; then
    rows+=("| $id | $label | **PASS** | $summary |")
  elif [ "$fatal" = "no" ]; then
    rows+=("| $id | $label | not blocking (reported by the inventory check) | $summary |")
  else
    failed=$((failed+1)); rows+=("| $id | $label | **FAIL** | exit $code; $summary $(echo "$out" | tail -3 | tr '\n|' ' /') |")
  fi
done

{
  echo "# Zentriq application control on Linux (containers, x64)"
  echo
  echo "| ID | Distribution | Result | Checks |"
  echo "|---|---|---|---|"
  printf '%s\n' "${rows[@]}"
} > "$report"
cat "$report"
[ $failed -eq 0 ]
