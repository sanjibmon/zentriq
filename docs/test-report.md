# Test report

Latest: GitHub Actions run 40197369473, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022 Datacenter
10.0.20348** VM. Release: `v1.1.1-build.85` (pre-release, **unsigned**), package version **1.1.1.85**. Results: unit +
PostgreSQL integration tests **332 PASS, 2 skipped** (live remote support and SNMP tests run in ci-quick: PASS, 334/334);
installer acceptance **65 passed, 0 failed**; in-place updates PASS (database migration V0027, schema 27).

New in build 85: **report time period** (From – to dates or a moving period such as last month, on any date column, saved
with the report and used by schedules, exports, dashboards and the data API), and **Backup & Restore** in the console:
backups of the whole installation (database, configuration, secrets, console certificate and CA, agent CA, MFA keys,
remote support data, report files), always encrypted, with an optional backup password; schedule (daily / weekdays /
monthly) and retention; destinations: local disk, SMB share, FTP/FTPS, SFTP, Azure Blob, Amazon S3 / S3-compatible,
Google Cloud Storage; history and download; restore from an uploaded file, this server or a destination.

| ID | Test | Result | Detail |
|---|---|---|---|
| T75 | Backup and restore | **PASS** | password and weekly schedule (validation); folder test OK; SMB share test OK; unreachable S3 refused; overview never shows a password; back up now copied to the folder and the SMB share (format 2, password, MFA key file); folder keeps 2 of 3; download; `backup-verify --password-file`; restored from the SMB share with the password: marker back, last restore and run history correct |
| T11 | Encrypted backup and deep restore validation | **PASS** | new file name, format 2 |
| — | Unit / PostgreSQL tests | **PASS** | report periods (relative ranges, custom inclusive dates, time zones, parameters only, DB filtering, summaries); backup crypto (install key or password, wrong password, no password, tampering and truncation, format 1 still opens); schedule slots and catch-up; file names and retention; folder, Azure (Shared Key signature recomputed), S3 (single and multipart, path-style, errors) and GCS (resumable pieces) targets; validation; data protection key ring through backup and restore on Windows (DPAPI); settings, destinations (encrypted secrets), history, unlock hand-off |

## Build and packaging

| Step | Result |
|---|---|
| `build.ps1` (.NET SDK, NuGet audit, warnings-as-errors for vulnerable packages, web console `tsc` + Vite build) | PASS |
| `test.ps1` – unit + PostgreSQL integration tests | PASS – total=45 passed=45 failed=0 |
| `package-windows.ps1` – server publish, Zentriq Agent (.NET Framework 4.8) build, ZentriqAgent-x64.msi, Zentriq.msi (ICE validation), Burn bundle, checksums, manifest | PASS |
| Code signing | NOT RUN – no signing certificate configured; artifacts are unsigned (Windows shows "Unknown publisher" / SmartScreen "Run anyway") |

New unit/integration tests in 1.1: Windows (WMI/registry), Linux (SSH output) and SNMP inventory mapping; enrollment
code format and server key pin; agent CA issue/validate/weak-key refusal; credential encryption bound to its record;
inventory persistence against PostgreSQL (agent + agentless records of one device merge into one asset, software list
replaced) and the trial's 30-device total cap.

## Release artifacts (SHA-256)

```
44bebfd8f8889f520d0c640ce93991bbce2d5bd815cfe043a8974bdd9bc8e1ee  Zentriq-1.1.1-docs.zip
a984a256f02732d2f4930fc1f048eebe1b87c694fb33252c16a3d2c0fdc45ad7  Zentriq-1.1.1-msi-offline-kit.zip
ba0cc35d6c1928eb91ae6dd228c2d4f3088d1bc0c34fcbf55f31b0774e1680d3  Zentriq-Setup-x64.exe
fd5e0905e5b469284c1d37b1a2393d88c7f04c50fff383d5ef6427f3861ba8c8  Zentriq.msi
68469f74122e677c26953e3ccedb500956408098d52500796c850a477ad36e4c  ZentriqAgent-x64.msi
```

## Installer acceptance (tests/installer/Invoke-InstallerAcceptance.ps1)

| ID | Test | Result | Detail |
|---|---|---|---|
| T01 | First-time installation (silent, answer file) | **PASS** | exit 0 in 2 min |
| T02 | PostgreSQL provisioned as a Windows service | **PASS** | running, loopback-only |
| T05 | API and worker services running under virtual accounts | **PASS** | ready: {"status":"ready","checks":{"database":"ok","schemaVersion":4,"expectedSchemaVersion":4,"initialized":true,"worker":"ok"}} |
| T03 | Database migrations applied | **PASS** | schema version 4 |
| T06 | Trial initialized for 14 days | **PASS** | expires 10/26/2026 05:43:12 |
| T04 | First administrator can sign in | **PASS** | signed in as admin@ci.zentriq.test |
| T07 | HTTPS certificate is valid and trusted on the server | **PASS** | TLS OK, firewall rule present |
| T22 | Start menu and Desktop shortcuts use the Zentriq icon | **PASS** | Start menu + Desktop shortcuts with zentriq.ico |
| T23 | Web console sign-in page is served (not the placeholder) | **PASS** | index + /assets/index-XBtEI3Cc.js (266 KB) |
| T08 | zentriqctl verify (end-to-end health) | **PASS** | all checks passed |
| T09 | Service restart does not reset the trial | **PASS** | trial unchanged |
| T10 | Repair keeps data and trial | **PASS** | repaired; trial unchanged; sign-in works |
| T11 | Encrypted backup and deep restore validation | **PASS** | zentriq-20261012-054348-ci.zbak (32 KB) |
| T12 | Tampered/unsigned license is rejected | **PASS** | rejected with 422 |
| T24 | Trial: every module enabled, 30 devices in total | **PASS** | limits: {"devices":30} |
| T25 | Company profile can be edited and read back | **PASS** | profile saved (Zentriq CI Private Limited) |
| T26 | Agent: enrollment code, MSI download from the console, silent install, inventory received | **PASS** | runnervmtn0fv: 73 applications, 16 GB RAM, health online; MSI signature: NotSigned |
| T27 | Agent port serves only the agent API and requires a client certificate | **PASS** | / 404, /api/v1 404, no cert 401, console port 404, firewall rule present |
| T28 | Agentless WinRM inventory with a stored credential; merges with the agent record | **PASS** | 172.17.96.1 via WinRM: 73 applications; assets 1 -> 1; Inventoried runnervmtn0fv over WinRM with credential 'ci-local-admin' (73 software items). |
| T29 | Agent revocation is enforced and uninstall removes the agent | **PASS** | revoked agent refused; uninstalled cleanly |
| T13 | Upgrade to the next release preserves data and trial | **PASS** | now 1.1.1 (database reports 1.1.1); trial unchanged |
| T14 | Logs contain no administrator password or connection-string passwords | **PASS** | 39 log files scanned |
| T15 | Uninstall removes the program but keeps customer data | **PASS** | program removed; data, backups and PostgreSQL kept |
| T16 | Reinstall reuses existing data without resetting the trial | **PASS** | data reused; trial unchanged; sign-in works |
| T17 | Restart of Windows (trial persists across reboot) | **NOT RUN** | CI runners cannot reboot mid-job; covered by T09/T10/T16 persistence checks. Run manually on a VM. |
| T18 | Trial expiry enforcement end-to-end (clock moved past 14 days) | **NOT RUN** | Covered by unit tests (LicenseEvaluator); not run against an installed server. |
| T19 | Valid commercial license activation end-to-end | **NOT RUN** | Requires the vendor private key; signer + verifier covered by unit tests. Run manually with docs/licensing.md. |
| T20 | AD/OU scan in a test domain | **NOT RUN** | No Active Directory domain on CI runners. |
| T30 | Agentless SSH (Linux) and SNMP collection against real devices | **NOT RUN** | No Linux/SNMP targets on the Windows CI runner; parsers covered by unit tests. Run manually. |
| T31 | Agent over the internet through a firewall/reverse proxy | **NOT RUN** | CI runs agent and server on one machine. Run manually with a published port 8444. |
| T21 | Interactive wizard (UI) walk-through | **NOT RUN** | Silent install exercises the same provisioning; UI pages need a manual run on a VM. |

Notes:
* T28 scans the CI machine itself; for that self-scan the test sets `DisableLoopbackCheck` (NTLM refuses authentication
  to a machine's own DNS alias). An earlier run without it failed with "Access is denied", which is how this
  limitation was found; it does not apply to scanning other machines.
* T26 originally failed: the enrollment code appeared in the agent MSI log (`RegAddValue`). Fixed by writing the
  code through a hidden deferred custom action; the test now asserts the code is absent from the log.
* The trial-policy migration for existing 1.0.x installations (250/25/50/50 → 30 devices) was verified on
  PostgreSQL 16 locally (old trial row updated, immutability trigger re-enabled). Upgrading a real 1.0.2 installation
  to 1.1.0 was **not** run in CI (CI upgrades 1.1.0 → 1.1.1).

## Not yet verified

| Item | Status |
|---|---|
| Interactive wizards (server and agent MSI dialogs) on a VM | NOT RUN – silent installs exercise the same actions |
| Upgrade from the published 1.0.2 build to 1.1.0 | NOT RUN in CI (migration verified on PostgreSQL directly) |
| Agentless SSH (Linux/macOS), SNMP, Active Directory sync/scan against real systems | NOT RUN – parsers unit-tested; AD connection errors tested (T37) |
| Password reset e-mail delivery through a real SMTP server | NOT RUN |
| Setup on Windows Server 2016, and refusal on Server 2012 R2 | NOT RUN – CI has Windows Server 2022 only |
| Firewalled-host detection (ARP/ICMP) on a real LAN | NOT RUN |
| Agent over the internet / through a reverse proxy, proxy (`PROXYURL`) | NOT RUN |
| Agent on Windows 10/11 clients and Server 2016/2019 | NOT RUN – CI used Windows Server 2022 |
| Windows reboot persistence | NOT RUN |
| Trial expiry and valid commercial license end-to-end | Unit tests only |
| Signed installers / SmartScreen behaviour | NOT RUN – needs a code-signing certificate |
| Installation with an existing PostgreSQL server | NOT RUN |
