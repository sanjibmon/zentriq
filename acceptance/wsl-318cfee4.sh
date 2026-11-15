set -e
mkdir -p /tmp/zqdummy/DEBIAN
printf 'Package: zq-ci-dummy\nVersion: 1.0\nArchitecture: all\nMaintainer: Zentriq CI <ci@example.com>\nDescription: Zentriq CI test package\n' > /tmp/zqdummy/DEBIAN/control
dpkg-deb --build /tmp/zqdummy /tmp/zq-ci-dummy.deb >/dev/null && dpkg -i /tmp/zq-ci-dummy.deb >/dev/null