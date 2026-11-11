#!/bin/bash
# Zentriq Agent for Linux on real distributions (containers): installs the package the way a customer would (deb, rpm or
# tar.gz), checks what the agent detects (--identify, --collect) and that the package removes cleanly.
# Usage: tests/linux-agent/test-distros.sh <packages dir> <report.md>
set -uo pipefail
pkg=$(cd "${1:?packages dir}" && pwd)
report=${2:?report}
mkdir -p "$pkg/results"
here=$(cd "$(dirname "$0")" && pwd)

# image | format | expected distribution | expected family | expected role | label | extra setup
cases=(
  "ubuntu:24.04|deb|ubuntu|debian|server|Ubuntu 24.04 LTS (server)|"
  "ubuntu:22.04|deb|ubuntu|debian|desktop|Ubuntu 22.04 LTS with a desktop|mkdir -p /usr/share/xsessions && touch /usr/share/xsessions/ubuntu.desktop && mkdir -p /etc/systemd/system && ln -sf /lib/systemd/system/gdm3.service /etc/systemd/system/display-manager.service"
  "debian:12|deb|debian|debian|server|Debian 12|"
  "debian:12|tar|debian|debian|server|Debian 12 (tar.gz install)|"
  "rockylinux:9|rpm|rocky|rhel|server|Rocky Linux 9|"
  "almalinux:9|rpm|almalinux|rhel|server|AlmaLinux 9|"
  "oraclelinux:9|rpm|ol|rhel|server|Oracle Linux 9|"
  "redhat/ubi9|rpm|rhel|rhel|server|Red Hat Enterprise Linux 9 (UBI)|"
  "fedora:41|rpm|fedora|fedora|server|Fedora 41 (container variant)|"
  "opensuse/leap:15.6|rpm|opensuse-leap|suse|server|openSUSE Leap 15.6|"
  "amazonlinux:2023|rpm|amzn|rhel|server|Amazon Linux 2023|"
)

install_cmd() {  # format
  case "$1" in
    deb) echo '(getent hosts archive.ubuntu.com >/dev/null || ! [ -e /etc/lsb-release ] || sed -i -E "s#http://(archive|security)\.ubuntu\.com#http://azure.archive.ubuntu.com#g" /etc/apt/sources.list /etc/apt/sources.list.d/*.sources 2>/dev/null; true) && apt-get update -qq >/dev/null && apt-get install -y -qq /pkg/zentriq-agent-linux-x64.deb >/tmp/install.log 2>&1 || { cat /tmp/install.log; exit 1; }' ;;
    rpm) echo 'if command -v dnf >/dev/null; then dnf install -y -q /pkg/zentriq-agent-linux-x64.rpm >/tmp/install.log 2>&1; elif command -v zypper >/dev/null; then zypper --non-interactive install --allow-unsigned-rpm /pkg/zentriq-agent-linux-x64.rpm >/tmp/install.log 2>&1; else yum install -y -q /pkg/zentriq-agent-linux-x64.rpm >/tmp/install.log 2>&1; fi || { cat /tmp/install.log; exit 1; }' ;;
    tar) echo '(command -v tar >/dev/null || (apt-get update -qq && apt-get install -y -qq tar gzip) >/dev/null) && mkdir -p /tmp/x && tar -xzf /pkg/zentriq-agent-linux-x64.tar.gz -C /tmp/x && /tmp/x/zentriq-agent/install.sh' ;;
  esac
}
remove_cmd() {
  case "$1" in
    deb) echo 'apt-get remove -y -qq zentriq-agent >/dev/null' ;;
    rpm) echo 'rpm -e zentriq-agent' ;;
    tar) echo '/opt/zentriq-agent/uninstall.sh >/dev/null' ;;
  esac
}

rows=(); failed=0; n=0
for c in "${cases[@]}"; do
  IFS='|' read -r image format dist fam role label setup <<<"$c"
  n=$((n+1)); id=$(printf 'D%02d' $n)
  name=$(echo "$image-$format" | tr '/:.' '---')
  echo "==== $id $label ($image, $format)"
  script="set -e; ${setup:+$setup;} $(install_cmd "$format"); zentriq-agent --version; zentriq-agent --identify; zentriq-agent --collect /pkg/results/$name.json >/dev/null; $(remove_cmd "$format"); test ! -e /usr/bin/zentriq-agent; echo REMOVED"
  out=$(docker run --rm -v "$pkg:/pkg" "$image" sh -c "$script" 2>&1); code=$?
  echo "$out" | tail -25
  detail=$(python3 "$here/check-inventory.py" "$pkg/results/$name.json" "$dist" "$fam" "$role" 2>&1); ok=$?
  if [ $code -ne 0 ] || [ $ok -ne 0 ] || ! echo "$out" | grep -q REMOVED; then
    failed=$((failed+1)); rows+=("| $id | $label | **FAIL** | exit $code; $(echo "$detail" | tr '\n|' ' /') $(echo "$out" | tail -3 | tr '\n|' ' /') |")
  else
    rows+=("| $id | $label | **PASS** | $(echo "$detail" | tr '\n|' ' /') |")
  fi
done

{
  echo "# Zentriq Agent for Linux on distributions (containers, x64)"
  echo
  echo "| ID | Distribution | Result | Detected |"
  echo "|---|---|---|---|"
  printf '%s\n' "${rows[@]}"
} > "$report"
cat "$report"
[ $failed -eq 0 ]
