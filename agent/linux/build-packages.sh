#!/bin/bash
# Builds the Zentriq Agent for Linux packages:
#   zentriq-agent-linux-{x64,arm64}.deb / .rpm / .tar.gz, version, install.sh, SHA256SUMS
# Usage: agent/linux/build-packages.sh <version> <output dir> [x64 arm64]
# Needs: .NET SDK, dpkg-deb, rpmbuild (Ubuntu: apt-get install rpm).
set -euo pipefail
version=${1:?version}
out=$(mkdir -p "${2:?output dir}" && cd "$2" && pwd)
shift 2
archs=${*:-x64 arm64}
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

for arch in $archs; do
  rid=linux-$arch
  deb_arch=$([ "$arch" = x64 ] && echo amd64 || echo arm64)
  rpm_arch=$([ "$arch" = x64 ] && echo x86_64 || echo aarch64)
  echo "== $rid"
  pub="$work/publish-$arch"
  dotnet publish "$repo/agent/Zentriq.Agent.Linux/Zentriq.Agent.Linux.csproj" -c Release -r "$rid" --self-contained true \
    -p:Version="$version" -o "$pub" --nologo -v quiet
  rm -f "$pub"/*.pdb "$pub"/*.dbg

  # Package root (shared by deb and rpm).
  root="$work/root-$arch"
  mkdir -p "$root/opt/zentriq-agent" "$root/usr/bin" "$root/usr/lib/systemd/system"
  cp -a "$pub"/. "$root/opt/zentriq-agent/"
  chmod 755 "$root/opt/zentriq-agent/zentriq-agent"
  ln -s /opt/zentriq-agent/zentriq-agent "$root/usr/bin/zentriq-agent"
  cp "$here"/systemd/*.service "$here"/systemd/*.timer "$root/usr/lib/systemd/system/"
  chmod 644 "$root"/usr/lib/systemd/system/*

  # .deb
  deb="$work/deb-$arch"
  cp -a "$root" "$deb"
  mkdir -p "$deb/DEBIAN"
  size=$(du -sk "$deb" --exclude=DEBIAN | cut -f1)
  cat > "$deb/DEBIAN/control" <<EOF
Package: zentriq-agent
Version: $version
Architecture: $deb_arch
Maintainer: Zentriq <support@zentriq.com>
Installed-Size: $size
Depends: libc6 (>= 2.27), libgcc-s1 | libgcc1, libstdc++6, zlib1g, libssl3 | libssl3t64 | libssl1.1, ca-certificates
Section: admin
Priority: optional
Homepage: https://zentriq.com
Description: Zentriq Agent - inventory and device management for Zentriq
 Collects hardware, software and security inventory for Zentriq and carries out the device actions an
 administrator requests in the Zentriq console. Connects out to the Zentriq server only (HTTPS, mutual TLS).
EOF
  install -m 755 "$here"/deb/postinst "$here"/deb/prerm "$here"/deb/postrm "$deb/DEBIAN/"
  dpkg-deb --root-owner-group -Zxz --build "$deb" "$out/zentriq-agent-$rid.deb" >/dev/null

  # .rpm
  top="$work/rpm-$arch"
  mkdir -p "$top"/{BUILD,RPMS,SOURCES,SPECS,SRPMS,BUILDROOT}
  sed -e "s|@VERSION@|$version|" -e "s|@ROOT@|$root|" "$here/rpm/zentriq-agent.spec" > "$top/SPECS/zentriq-agent.spec"
  rpmbuild -bb --quiet --target "$rpm_arch" --define "_topdir $top" --define "_build_name_fmt %{NAME}.rpm" \
    --define "_binary_payload w6.xzdio" "$top/SPECS/zentriq-agent.spec"
  cp "$top/RPMS/zentriq-agent.rpm" "$out/zentriq-agent-$rid.rpm"

  # .tar.gz (zentriq-agent/{bin,systemd,install.sh,uninstall.sh})
  t="$work/tar-$arch/zentriq-agent"
  mkdir -p "$t/bin" "$t/systemd"
  cp -a "$pub"/. "$t/bin/"
  cp "$here"/systemd/* "$t/systemd/"
  install -m 755 "$here"/tar/install.sh "$here"/tar/uninstall.sh "$t/"
  tar -C "$work/tar-$arch" --owner=0 --group=0 -czf "$out/zentriq-agent-$rid.tar.gz" zentriq-agent
done

echo "$version" > "$out/version"
install -m 644 "$here/install.sh" "$out/install.sh"
(cd "$out" && sha256sum zentriq-agent-* > SHA256SUMS)
ls -la "$out"
