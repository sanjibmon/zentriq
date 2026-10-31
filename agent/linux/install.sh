#!/bin/sh
# Zentriq Agent for Linux: one-line installer, served by the Zentriq server on its agent port
# (/api/agent/v1/linux/install.sh). The console shows the command, e.g.
#   curl -fsSLk --pinnedpubkey 'sha256//<pin>' https://zentriq.example.com:8444/api/agent/v1/linux/install.sh \
#     | sudo sh -s -- --server https://zentriq.example.com:8444 --code XXXXX-XXXXX-XXXXX-XXXXX --pin '<pin>'
# It picks the package for this device (deb on Debian / Ubuntu and relatives, rpm on RHEL / Fedora / SUSE /
# Amazon Linux and relatives, tar.gz elsewhere; x64 or ARM64), downloads it from the same server trusting only the
# server's pinned key, checks its SHA-256, installs it and enrolls the agent with the code.
set -eu

server=""; code=""; pin=""; checkin=""; inventory=""; proxy=""; format=""
while [ $# -gt 0 ]; do
  case "$1" in
    --server) server="$2"; shift ;;
    --code) code="$2"; shift ;;
    --pin) pin="$2"; shift ;;
    --checkin) checkin="$2"; shift ;;
    --inventory) inventory="$2"; shift ;;
    --proxy) proxy="$2"; shift ;;
    --format) format="$2"; shift ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
fail() { echo "Zentriq Agent: $*" >&2; exit 1; }
[ "$(id -u)" = "0" ] || fail "run as root (… | sudo sh -s -- …)."
[ -n "$server" ] || fail "--server https://<server>:<agent port> is required."
server=${server%/}

case "$(uname -m)" in
  x86_64|amd64) platform=linux-x64 ;;
  aarch64|arm64) platform=linux-arm64 ;;
  *) fail "this processor ($(uname -m)) is not supported (x64 and ARM64 are)." ;;
esac
if [ -z "$format" ]; then
  if command -v dpkg >/dev/null 2>&1 && command -v apt-get >/dev/null 2>&1; then format=deb
  elif command -v rpm >/dev/null 2>&1 && { command -v dnf >/dev/null 2>&1 || command -v yum >/dev/null 2>&1 || command -v zypper >/dev/null 2>&1; }; then format=rpm
  else format=tar; fi
fi
if ldd --version 2>&1 | grep -qi musl; then fail "musl-based systems (Alpine) are not supported; use a glibc distribution."; fi

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
fetch() {  # fetch <url> <file>
  if command -v curl >/dev/null 2>&1; then
    if [ -n "$pin" ]; then curl -fsSLk --pinnedpubkey "sha256//$pin" ${proxy:+--proxy "$proxy"} -o "$2" "$1"
    else curl -fsSL ${proxy:+--proxy "$proxy"} -o "$2" "$1"; fi
  elif command -v wget >/dev/null 2>&1; then
    [ -z "$pin" ] || fail "curl is needed to check the server's key; install curl and run the command again."
    wget -q ${proxy:+-e use_proxy=yes -e https_proxy="$proxy"} -O "$2" "$1"
  else fail "curl or wget is needed."; fi
}
ext=$format; [ "$format" = tar ] && ext=tar.gz
file="$tmp/zentriq-agent-$platform.$ext"
echo "Downloading the Zentriq Agent ($platform, $format) from $server ..."
fetch "$server/api/agent/v1/linux/package?platform=$platform&format=$format" "$file" || fail "download failed."
fetch "$server/api/agent/v1/linux/package.sha256?platform=$platform&format=$format" "$tmp/sha" || fail "checksum download failed."
want=$(tr -d ' \r\n' < "$tmp/sha")
have=$(sha256sum "$file" | cut -d' ' -f1)
[ "$want" = "$have" ] || fail "the package checksum does not match; nothing installed."

echo "Installing ..."
case "$format" in
  deb) apt-get install -y -q "$file" >/dev/null 2>&1 || dpkg -i "$file" || { apt-get -f install -y -q && dpkg -i "$file"; } ;;
  rpm)
    if command -v dnf >/dev/null 2>&1; then dnf install -y -q "$file"
    elif command -v zypper >/dev/null 2>&1; then zypper --non-interactive install --allow-unsigned-rpm "$file"
    elif command -v yum >/dev/null 2>&1; then yum install -y -q "$file"
    else rpm -U --replacepkgs "$file"; fi ;;
  tar) mkdir -p "$tmp/x" && tar -xzf "$file" -C "$tmp/x" && "$tmp/x/zentriq-agent/install.sh" ;;
esac
[ -x /opt/zentriq-agent/zentriq-agent ] || fail "installation failed."

if [ -n "$code" ]; then
  # shellcheck disable=SC2086
  /opt/zentriq-agent/zentriq-agent --enroll "$code" --server "$server" ${checkin:+--checkin "$checkin"} ${inventory:+--inventory "$inventory"} ${proxy:+--proxy "$proxy"}
fi
echo "Done. Check with: sudo zentriq-agent --status   (what it detected: sudo zentriq-agent --identify)"
