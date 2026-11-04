# Zentriq Agent for Linux (.rpm). Built by agent/linux/build-packages.sh from the published files (no compiling here).
%global debug_package %{nil}
%global __strip /bin/true
%global _build_id_links none
%define __os_install_post %{nil}

Name:           zentriq-agent
Version:        @VERSION@
Release:        1
Summary:        Zentriq Agent: inventory and device management for Zentriq
License:        Proprietary
URL:            https://zentriq.com
AutoReqProv:    no
Requires:       glibc, ca-certificates
Requires:       (libstdc++ or libstdc++6)
Requires:       (zlib or libz1)
Requires:       (openssl-libs or libopenssl3 or libopenssl1_1)

%description
The Zentriq Agent collects hardware, software and security inventory for Zentriq and carries out the device actions
an administrator requests in the Zentriq console. It connects out to the Zentriq server only (HTTPS, mutual TLS).

%install
cp -a @ROOT@/. %{buildroot}/

%files
/opt/zentriq-agent
/usr/bin/zentriq-agent
/usr/lib/systemd/system/zentriq-agent.service
/usr/lib/systemd/system/zentriq-agent-watchdog.service
/usr/lib/systemd/system/zentriq-agent-watchdog.timer

%post
echo rpm > /opt/zentriq-agent/installed-by
mkdir -p /var/lib/zentriq-agent /var/log/zentriq-agent
chmod 700 /var/lib/zentriq-agent
chmod 750 /var/log/zentriq-agent
if [ -d /run/systemd/system ]; then
  rm -f /etc/systemd/system/zentriq-agent.service.d/10-protection.conf
  systemctl daemon-reload >/dev/null 2>&1 || :
  systemctl enable zentriq-agent.service zentriq-agent-watchdog.timer >/dev/null 2>&1 || :
  systemctl restart zentriq-agent.service >/dev/null 2>&1 || :
  systemctl start zentriq-agent-watchdog.timer >/dev/null 2>&1 || :
fi
if [ "$1" = "1" ] && [ -n "$ZENTRIQ_ENROLLMENT_CODE" ] && [ -n "$ZENTRIQ_SERVER" ]; then
  /opt/zentriq-agent/zentriq-agent --enroll "$ZENTRIQ_ENROLLMENT_CODE" --server "$ZENTRIQ_SERVER" || :
fi
exit 0

%preun
# $1 = 0: removal (needs a code while tamper protection is on); 1: upgrade.
if [ "$1" = "0" ]; then
  if [ -x /opt/zentriq-agent/zentriq-agent ]; then
    /opt/zentriq-agent/zentriq-agent --check-uninstall || exit 1
    /opt/zentriq-agent/zentriq-agent --cleanup-hooks >/dev/null 2>&1 || :
  fi
  if [ -d /run/systemd/system ]; then
    rm -f /etc/systemd/system/zentriq-agent.service.d/10-protection.conf
    systemctl daemon-reload >/dev/null 2>&1 || :
    systemctl disable --now zentriq-agent-watchdog.timer zentriq-agent.service >/dev/null 2>&1 || :
  fi
else
  if [ -d /run/systemd/system ]; then
    rm -f /etc/systemd/system/zentriq-agent.service.d/10-protection.conf
    systemctl daemon-reload >/dev/null 2>&1 || :
  fi
fi
exit 0

%postun
if [ "$1" = "0" ]; then
  rm -rf /var/lib/zentriq-agent /var/log/zentriq-agent /var/spool/zentriq-agent /etc/systemd/system/zentriq-agent.service.d
  rm -f /opt/zentriq-agent/installed-by
fi
if [ -d /run/systemd/system ]; then systemctl daemon-reload >/dev/null 2>&1 || :; fi
exit 0
