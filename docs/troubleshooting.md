# Troubleshooting

Start with `zentriqctl verify` and `C:\ProgramData\Zentriq\Logs\install-result.json` (failed step, error, remedy,
log path). `zentriqctl diagnose` collects everything support needs (redacted).

## Setup failed

Setup prints *which step* failed. Program files are rolled back; PostgreSQL, the database and the data folder are
kept, so fixing the cause and running setup again continues where it stopped (the trial has not started unless the
"first administrator" step succeeded).

| Step / message | Likely cause | Fix |
|---|---|---|
| System check FAIL (OS, memory, disk) | Unsupported server | Use Windows Server 2019+ x64, 4 GB+, 2 GB+ free |
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
