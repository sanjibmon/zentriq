# Troubleshooting

Start with `zentriqctl verify` and `C:\ProgramData\Zentriq\Logs\install-result.json` (failed step, error, remedy,
log path). `zentriqctl diagnose` collects everything support needs (redacted).

## Setup failed

Setup prints *which step* failed. Program files are rolled back; PostgreSQL, the database and the data folder are
kept, so fixing the cause and running setup again continues where it stopped (the trial has not started unless the
"first administrator" step succeeded).

| Step / message | Likely cause | Fix |
|---|---|---|
| "The minimum operating system requirement is Windows Server 2016 (any edition)" | Windows Server 2012 R2 or older | Use Windows Server 2016/2019/2022/2025 x64 |
| System check FAIL (OS, memory, disk) | Unsupported server | Use Windows Server 2016+ x64, 4 GB+, 2 GB+ free |
| "PostgreSQL installer is not available" | Ran `Zentriq.msi` alone | Use `Zentriq-Setup-x64.exe`, or the offline kit with `prereqs\` next to the MSI, or choose an existing server |
| "PostgreSQL installation failed (exit code …)" | Port in use, non-empty data folder, missing VC++ runtime, antivirus | See `%TEMP%\install-postgresql.log`; choose another port/empty folder; install `vc_redist.x64.exe` |
| "service postgresql-x64-17 exists but its superuser password is not known" | PostgreSQL 17 installed by something else | Choose *Use an existing PostgreSQL server* and supply an admin role, or remove the other installation |
| "PostgreSQL rejected the superuser credentials" / "Cannot connect" | Existing server: wrong password, `pg_hba.conf`, firewall | Allow this server with `scram-sha-256` in `pg_hba.conf`; check `listen_addresses` |
| "A Zentriq service did not start" / "API did not become ready" | Port in use (IIS on 443), certificate access, DB permissions | `netstat -abno | findstr :443`; check `Logs\api-*.log` and the Application event log; choose another port |
| "worker self-test job" | Worker can't reach DB or crashed | `Logs\worker-*.log` |
| "Initialization was rejected" | Administrator password policy | 12+ chars, three character classes, not containing the e-mail name |

Logs: `%TEMP%\Zentriq-Setup_*.log` (bootstrapper), `%TEMP%\Zentriq-Setup_*_ZentriqMsi.log` (MSI, verbose),
`C:\ProgramData\Zentriq\Logs\provision-*.log` (each provisioning step with timings).

## After installation

* **Browser certificate warning** – the self-signed certificate is trusted on the server only; see administrator-guide.md.
* **"This feature is not available under the current license" (403)** – trial or license expired, or the module is not licensed. Import a license; data remains accessible.
* **Clock rollback** – server time moved backwards >24 h; correct the time (NTP). Features resume automatically.
* **Jobs stay queued** – `Get-Service ZentriqWorker`; worker heartbeat in `zentriqctl verify`. Jobs whose worker died are re-queued after their lease expires.
* **Backups on demand never run** – the `\Zentriq\Maintenance` scheduled task must be enabled (`schtasks /Query /TN "\Zentriq\Maintenance"`).

## Inventory collection

* **Scan result "Credentials rejected … Access is denied" (Windows)** – the account is not a local administrator on the
  target, or it is a local account blocked by UAC remote restrictions (set `LocalAccountTokenFilterPolicy = 1`), or
  NTLM was refused (use a host name with Kerberos in a domain, or add the targets to WinRM `TrustedHosts` / use
  WinRM over HTTPS when scanning by IP address).
* **"No selected credential matches the protocols this host exposes"** – WinRM (5985/5986), RPC (135) or SSH (22)
  are not reachable from the Zentriq server; open the firewall or install the agent.
* **SSH "host key … changed"** – the device was rebuilt or something is impersonating it. Verify, then clear the key
  under *Discovery › Credentials › SSH host keys*.
* **Scan finished but no hardware/software** – the scan had no credential selected, or the credential could not log
  in. Open the scan (click its name) to see the reason per device. Select a credential in *New scan*.
* **"Host is up … every probed port is blocked by a firewall"** – the device answered ping/ARP but no management
  port. Enable WinRM (see agents-and-agentless.md › Preparing Windows computers) or install the agent.
* **Agent installed but the device is not under Agents; agent.log says "ServicePointManager does not support proxies
  with the https scheme"** – a proxy was entered at installation. Fix: elevated PowerShell on the device:
  `Remove-ItemProperty HKLM:\SOFTWARE\Zentriq\Agent -Name ProxyUrl; Restart-Service ZentriqAgent`, or reinstall with
  the current agent MSI (which ignores such proxies and validates the field).
* **Active Directory "LDAP 49 invalid credentials"** – wrong user ID or password. **"Cannot reach the domain
  controller"** – wrong address, firewall, or (LDAPS) the DC has no certificate / the certificate is not trusted by the
  Zentriq server: use LDAP 389 (signed and sealed) or untick *Verify certificate* for a test.
* **Forgot password: "not set up on this server"** – configure *System Settings › E-mail (SMTP)*, or reset with
  `zentriqctl reset-password --email …` on the server.
* **Agent shows "Never checked in"** – on the device run `"C:\Program Files\Zentriq Agent\ZentriqAgent.exe" --status`
  and see `C:\ProgramData\Zentriq\Agent\Logs\agent.log`. Typical causes: port 8444 not reachable, proxy needed
  (`PROXYURL`), expired/used-up enrollment code, .NET Framework 4.8 missing.
