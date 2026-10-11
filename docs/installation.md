# Installing Zentriq Console

## What you get

| File | Use it when |
|---|---|
| **Zentriq-Setup-x64.exe** | Normal installation. One offline file containing the Visual C++ runtime, PostgreSQL 17 and Zentriq. Shows the setup wizard, or runs silently. |
| **Zentriq.msi** | Software-distribution tools (SCCM/Intune/GPO) or when you use **your own PostgreSQL server**. Contains Zentriq only. To install PostgreSQL with it, use the *offline kit* (`Zentriq-<version>-msi-offline-kit.zip`): keep `prereqs\` next to `Zentriq.msi` and install `prereqs\vc_redist.x64.exe` first. |
| `SHA256SUMS.txt`, `release-manifest.json` | Verify downloads: `Get-FileHash Zentriq-Setup-x64.exe -Algorithm SHA256` must match. The manifest lists the bundled third-party installers with their verified publishers and hashes. |

**EXE vs MSI.** The EXE is a WiX *Burn* bootstrapper: it installs prerequisites (Visual C++ runtime), caches the
PostgreSQL installer next to the MSI and then runs `Zentriq.msi` with its full wizard. The MSI is the actual Zentriq
product package (files, services, firewall rule, registry) and runs the configuration engine `zentriqctl`. Both
end with the same verified installation. Both appear as one entry ("Zentriq Console") in *Apps & features*.

## Requirements

* Windows Server 2019, 2022 or 2025 (64-bit), Desktop Experience or Server Core. Windows 10/11 is accepted for evaluation only.
* 4 GB RAM minimum (8 GB+ recommended), 2 CPU cores, 10 GB free disk (more for data and backups).
* Local administrator rights. Domain administrator rights are **not** needed and the services never run as one.
* A free TCP port for HTTPS (default 443) and, for the bundled database, 5432.
* No internet access is needed.

## Interactive installation

Run `Zentriq-Setup-x64.exe` as an administrator. The wizard:

1. **Welcome** and **license agreement**.
2. **System check** – OS, memory, disk, ports, PostgreSQL, existing Zentriq data. Items marked *FAIL* block setup; *WARN* items are advisory. *Re-check* after fixing.
3. **Folders** – program files (default `C:\Program Files\Zentriq`) and the data folder (default `C:\ProgramData\Zentriq`), which contains `Config`, `Secrets`, `Logs`, `Packages`, `Exports` and `Backups`. Data is never stored with the program files.
4. **PostgreSQL** – *Install PostgreSQL 17 on this server* (choose port and data folder; it can be on another volume, e.g. `D:\PostgreSQL\17\data`) or *Use an existing PostgreSQL server* (14–18; host, port, and a role with CREATEROLE/CREATEDB used once and not stored).
5. **HTTPS and network access** – host name users will type, HTTPS port, certificate (self-signed, or an existing certificate in *Local Computer › Personal* by thumbprint) and the Windows Firewall rule (any network / local subnet / none).
6. **First administrator** – organization, name, sign-in e-mail and password (12+ characters, three character classes).
7. **Ready** → **Progress**. Setup installs PostgreSQL, creates the database and least-privilege roles, applies migrations, issues/binds the certificate, starts the services, has the worker run a self-test job, creates the administrator, starts the 14-day trial and verifies the console over HTTPS.
8. **Finish** – opens the console URL.

If existing Zentriq data is found (reinstall, upgrade), pages 4–6 are skipped and the saved settings are reused.

**Setup reports success only after every verification passes.** If a step fails you get the failed step, the reason, what to do and the log path; Windows Installer then removes the program files, while PostgreSQL, the database and the data folder are kept, so running setup again continues where it stopped. See [troubleshooting.md](troubleshooting.md).

## Silent installation

Create an answer file (protect it – it contains the administrator password – and delete it afterwards):

```json
{
  "postgres": { "mode": "bundled", "port": 5432, "dataDir": "D:\\PostgreSQL\\17\\data" },
  "https":    { "port": 443, "hostName": "zentriq.corp.example.com" },
  "admin":    { "organization": "Contoso", "email": "it-admin@contoso.com",
                "displayName": "IT Administrator", "password": "<strong password>" }
}
```

```powershell
.\Zentriq-Setup-x64.exe /quiet /norestart /log C:\Temp\zentriq-setup.log ZQ_ANSWERFILE=C:\Temp\answers.json
$LASTEXITCODE   # 0 = installed and verified, 3010 = reboot required (VC++ runtime), anything else = failed
```

Existing PostgreSQL: `"postgres": { "mode": "existing", "host": "pg01", "port": 5432, "superUser": "zentriq_setup", "superPassword": "...", "sslMode": "Require" }`.
Existing certificate: `"https": { ..., "certificateThumbprint": "<40 hex chars>" }`.

MSI only (PostgreSQL already present, or prereqs\ next to the MSI):

```powershell
msiexec /i Zentriq.msi /qn /l*v C:\Temp\zentriq-msi.log ZQ_ANSWERFILE=C:\Temp\answers.json DATAFOLDER=E:\ZentriqData FIREWALLSCOPE=localsubnet
```

Public MSI properties: `INSTALLFOLDER`, `DATAFOLDER`, `PGMODE` (bundled|existing), `PGHOST`, `PGPORT`, `PGSUPERUSER`,
`PGSUPERPASSWORD`, `PGDATADIR`, `PGINSTALLDIR`, `HTTPSHOSTNAME`, `HTTPSPORT`, `CERTMODE` (selfsigned|existing),
`CERTTHUMBPRINT`, `FIREWALLSCOPE` (any|localsubnet|none), `ADMINORG`, `ADMINNAME`, `ADMINEMAIL`, `ADMINPASSWORD`,
`ZQ_ANSWERFILE`. Password properties are hidden from the MSI log; prefer the answer file so passwords do not appear on a command line.

## After installation

* Open `https://<host name>/` and sign in. With a self-signed certificate, browsers on other machines will warn until you replace the certificate or trust it (see [administrator-guide.md](administrator-guide.md)).
* **Move the backup recovery key** `C:\ProgramData\Zentriq\Secrets\backup-recovery-key.txt` to offline storage and delete it from the server. Without it, backups cannot be restored on another server.
* `"C:\Program Files\Zentriq\tools\zentriqctl.exe" verify` re-runs the health checks at any time.
* Installation report: `C:\ProgramData\Zentriq\Logs\install-result.json` and `provision-*.log`.

## What is installed where

| Item | Location / identity |
|---|---|
| Program files | `C:\Program Files\Zentriq\{api,worker,tools,docs}` (self-contained .NET; no .NET runtime install needed) |
| Services | `ZentriqApi` (NT SERVICE\ZentriqApi), `ZentriqWorker` (NT SERVICE\ZentriqWorker): automatic (delayed) start, restart on failure, depend on PostgreSQL when bundled |
| PostgreSQL | `postgresql-x64-17` (NETWORK SERVICE), listening on localhost only; roles `zentriq_owner` (schema owner, used by setup/maintenance) and `zentriq_app` (application, no DDL) |
| Data | `C:\ProgramData\Zentriq` (ACL: SYSTEM, Administrators; services only where needed) |
| Scheduled tasks | `\Zentriq\Maintenance` (every 5 min: privileged jobs such as on-demand backups), `\Zentriq\Nightly backup` (01:30, keeps 14) |
| Firewall | Inbound TCP on the HTTPS port (scope as chosen) |
| Registry | `HKLM\SOFTWARE\Zentriq\Console` |
