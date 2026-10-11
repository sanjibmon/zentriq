# Administrator guide

## Day-to-day commands (`C:\Program Files\Zentriq\tools\zentriqctl.exe`, elevated prompt)

| Command | Purpose |
|---|---|
| `zentriqctl status` | One-line health per component |
| `zentriqctl verify` | Full end-to-end check (services, PostgreSQL as the app role, schema, worker heartbeat, HTTPS readiness, license state, console page) – exit code 0/1 |
| `zentriqctl diagnose` | Writes a redacted `Logs\diagnostics-<time>.zip` for support (no secrets files, passwords masked) |
| `zentriqctl repair` | Re-runs provisioning with the saved settings (re-applies ACLs, roles, migrations, certificate binding, service recovery, scheduled tasks). Same as *Repair* in *Apps & features* |
| `zentriqctl backup` / `backup-verify` / `restore` | See [backup-restore.md](backup-restore.md) |

## Services

| Service | Account | Notes |
|---|---|---|
| Zentriq API (`ZentriqApi`) | `NT SERVICE\ZentriqApi` | HTTPS console + REST API. Delayed automatic start, restart after 5 s / 15 s / 60 s on failure |
| Zentriq Worker (`ZentriqWorker`) | `NT SERVICE\ZentriqWorker` | Durable jobs (discovery, reports, usage snapshots). Jobs survive restarts: leases expire and jobs are re-queued |
| PostgreSQL (`postgresql-x64-17`) | `NT AUTHORITY\NetworkService` | Bundled server, localhost only |

Both Zentriq services depend on the bundled PostgreSQL service. Stopping order for maintenance: API, Worker, PostgreSQL.

## Logs

`C:\ProgramData\Zentriq\Logs`: `api-*.log`, `worker-*.log` (JSON lines, daily files), `provision-*.log` (setup/repair),
`backup-*.log`, `maintenance-*.log`, `install-result.json` (last setup outcome). Secrets are redacted before writing.
Setup logs: `%TEMP%\Zentriq-Setup_*.log` (bootstrapper and MSI), `%TEMP%\install-postgresql.log` (PostgreSQL installer).

## Replacing the HTTPS certificate

1. Import the certificate (with private key) into *Local Computer › Personal* and note its thumbprint.
2. Run `Zentriq-Setup-x64.exe` → *Repair* is not enough to change settings; instead edit
   `C:\ProgramData\Zentriq\Config\zentriq.json`: set `"Https": { "CertificateThumbprint": "<thumbprint>" }`,
   then run `zentriqctl repair` (it validates the certificate and grants the API service read access to its private key).
3. To change the host name or port, edit `HostName`/`Port` in the same file, update the firewall rule
   (`Get-NetFirewallRule -DisplayName "Zentriq Console*"`), and run `zentriqctl repair`.

Self-signed certificate: setup adds it to *Trusted Root* on the server only. To avoid browser warnings elsewhere,
distribute `C:\ProgramData\Zentriq\Secrets\https.pfx`'s public certificate via Group Policy or replace it as above.

## Moving data

PostgreSQL data can be placed on another volume at installation. To move the Zentriq data folder later: stop the
services, move the folder (preserving ACLs, e.g. `robocopy /E /COPYALL /MOVE`), set
`HKLM\SOFTWARE\Zentriq\Console\DataDirectory`, run `zentriqctl repair --data-dir <new path>`.

## Uninstalling

*Apps & features › Zentriq Console › Uninstall* (or `Zentriq-Setup-x64.exe /uninstall /quiet`) removes program
files, services, firewall rule and scheduled tasks. It **does not delete** customer data, backups, secrets, the
certificate or the PostgreSQL server/database, so a later installation continues with the same data and trial.

Complete removal (irreversible – take and export a backup first):
1. Uninstall Zentriq as above.
2. Uninstall *PostgreSQL 17* from *Apps & features* (if it was installed for Zentriq) and delete its data folder.
3. Delete `C:\ProgramData\Zentriq` and `HKLM\SOFTWARE\Zentriq`.
4. Remove the Zentriq self-signed certificate from *Local Computer › Trusted Root*.

## Account and access

* Sign-in sessions: HttpOnly, Secure, SameSite=Strict cookie, 30 minutes sliding. Login attempts are rate limited.
* MFA (TOTP) can be enrolled per user and required by policy.
* RBAC is enforced by the API for every endpoint; the UI only hides what the API would refuse.
* Every security-relevant action is written to the audit log (`/api/v1/audit`).
