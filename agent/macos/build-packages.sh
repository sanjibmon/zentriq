#!/bin/bash
# Builds the Zentriq Agent for macOS packages on a Mac (pkgbuild, codesign):
#   zentriq-agent-macos-{x64,arm64}.pkg, version, install.sh, SHA256SUMS
# Usage: agent/macos/build-packages.sh <version> <output dir> [x64 arm64]
# The programs carry an ad-hoc signature (required to run on Apple silicon). For distribution outside the one-line
# installer (a .pkg opened in Finder, MDM), sign with a Developer ID certificate and notarize: set
# ZENTRIQ_MAC_APP_SIGN_ID / ZENTRIQ_MAC_PKG_SIGN_ID to the identities in the keychain.
set -euo pipefail
version=${1:?version}
out=$(mkdir -p "${2:?output dir}" && cd "$2" && pwd)
shift 2
archs=${*:-x64 arm64}
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
appsign=${ZENTRIQ_MAC_APP_SIGN_ID:--}

for arch in $archs; do
  rid=osx-$arch
  echo "== $rid"
  pub="$work/publish-$arch"
  dotnet publish "$repo/agent/Zentriq.Agent.Linux/Zentriq.Agent.Linux.csproj" -c Release -r "$rid" --self-contained true \
    -p:Version="$version" -o "$pub" --nologo -v quiet
  rm -f "$pub"/*.pdb "$pub"/*.dbg
  rm -rf "$pub"/*.dSYM

  root="$work/root-$arch"
  mkdir -p "$root/Library/Zentriq/Agent" "$root/Library/LaunchDaemons" "$root/usr/local/bin"
  cp -a "$pub"/. "$root/Library/Zentriq/Agent/"
  install -m 755 "$here/uninstall.sh" "$root/Library/Zentriq/Agent/uninstall.sh"
  echo pkg > "$root/Library/Zentriq/Agent/installed-by"
  install -m 644 "$here/com.zentriq.agent.plist" "$root/Library/LaunchDaemons/com.zentriq.agent.plist"
  ln -s /Library/Zentriq/Agent/zentriq-agent "$root/usr/local/bin/zentriq-agent"
  for f in "$root/Library/Zentriq/Agent/zentriq-agent" "$root"/Library/Zentriq/Agent/*.dylib; do
    [ -f "$f" ] || continue
    if [ "$appsign" = "-" ]; then codesign --force --sign - "$f"
    else codesign --force --options runtime --timestamp --sign "$appsign" "$f"; fi
  done
  codesign --verify --verbose=1 "$root/Library/Zentriq/Agent/zentriq-agent"
  lipo -archs "$root/Library/Zentriq/Agent/zentriq-agent"

  pkg="$out/zentriq-agent-macos-$arch.pkg"
  sign=()
  [ -n "${ZENTRIQ_MAC_PKG_SIGN_ID:-}" ] && sign=(--sign "$ZENTRIQ_MAC_PKG_SIGN_ID")
  pkgbuild --root "$root" --scripts "$here/scripts" --identifier com.zentriq.agent --version "$version" \
    --install-location / --ownership recommended ${sign[@]+"${sign[@]}"} "$pkg"
done

echo "$version" > "$out/version"
install -m 644 "$here/install.sh" "$out/install.sh"
(cd "$out" && shasum -a 256 zentriq-agent-macos-* > SHA256SUMS)
ls -la "$out"
