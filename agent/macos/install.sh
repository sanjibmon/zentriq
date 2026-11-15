#!/bin/sh
# Zentriq Agent for macOS: one-line installer, served by the Zentriq server on its agent port
# (/api/agent/v1/macos/install.sh). The console shows the command, e.g.
#   curl -fsSLk --pinnedpubkey 'sha256//<pin>' https://zentriq.example.com:8444/api/agent/v1/macos/install.sh \
#     | sudo sh -s -- --server https://zentriq.example.com:8444 --code XXXXX-XXXXX-XXXXX-XXXXX --pin '<pin>'
# It downloads the package for this Mac (Apple silicon or Intel) from the same server trusting only the server's
# pinned key, checks its SHA-256, installs it and enrolls the agent with the code.
set -eu

server=""; code=""; pin=""; checkin=""; inventory=""; proxy=""
while [ $# -gt 0 ]; do
  case "$1" in
    --server) server="$2"; shift ;;
    --code) code="$2"; shift ;;
    --pin) pin="$2"; shift ;;
    --checkin) checkin="$2"; shift ;;
    --inventory) inventory="$2"; shift ;;
    --proxy) proxy="$2"; shift ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
fail() { echo "Zentriq Agent: $*" >&2; exit 1; }
[ "$(uname -s)" = "Darwin" ] || fail "this installer is for macOS."
[ "$(id -u)" = "0" ] || fail "run as root (… | sudo sh -s -- …)."
[ -n "$server" ] || fail "--server https://<server>:<agent port> is required."
server=${server%/}
major=$(sw_vers -productVersion | cut -d. -f1)
[ "$major" -ge 12 ] 2>/dev/null || fail "macOS 12 (Monterey) or later is needed."

case "$(uname -m)" in
  arm64) platform=macos-arm64 ;;
  x86_64) if [ "$(sysctl -in sysctl.proc_translated 2>/dev/null)" = "1" ]; then platform=macos-arm64; else platform=macos-x64; fi ;;
  *) fail "this processor ($(uname -m)) is not supported." ;;
esac

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
fetch() {  # fetch <url> <file>
  if [ -n "$pin" ]; then curl -fsSLk --pinnedpubkey "sha256//$pin" ${proxy:+--proxy "$proxy"} -o "$2" "$1"
  else curl -fsSL ${proxy:+--proxy "$proxy"} -o "$2" "$1"; fi
}
file="$tmp/zentriq-agent-$platform.pkg"
echo "Downloading the Zentriq Agent ($platform) from $server ..."
fetch "$server/api/agent/v1/macos/package?platform=$platform&format=pkg" "$file" || fail "download failed."
fetch "$server/api/agent/v1/macos/package.sha256?platform=$platform&format=pkg" "$tmp/sha" || fail "checksum download failed."
want=$(tr -d ' \r\n' < "$tmp/sha")
have=$(shasum -a 256 "$file" | cut -d' ' -f1)
[ "$want" = "$have" ] || fail "the package checksum does not match; nothing installed."

echo "Installing ..."
/usr/sbin/installer -pkg "$file" -target / >/dev/null || fail "installation failed."
[ -x /Library/Zentriq/Agent/zentriq-agent ] || fail "installation failed."

if [ -n "$code" ]; then
  # shellcheck disable=SC2086
  /Library/Zentriq/Agent/zentriq-agent --enroll "$code" --server "$server" ${checkin:+--checkin "$checkin"} ${inventory:+--inventory "$inventory"} ${proxy:+--proxy "$proxy"}
fi
echo "Done. Check with: sudo zentriq-agent --status"
