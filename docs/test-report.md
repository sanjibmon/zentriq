# Test report

Latest: GitHub Actions run 40212734306, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022 Datacenter
10.0.20348** VM. Release: `v1.1.1-build.87` (pre-release, **unsigned**), package version **1.1.1.87**. Results: unit +
PostgreSQL integration tests **343 PASS, 2 skipped** (live remote support and SNMP tests run in ci-quick: PASS, 345/345);
installer acceptance **67 passed, 0 failed**; in-place updates PASS (database migration V0028, schema 28). Build 86 stopped
at T55 (an API token with *Export reports* could no longer reach report data); fixed in build 87.

New in build 87: **role-based access per area** (dashboard, IT inventory, non-IT inventory, IT accessories, lifecycle, cloud,
activity log, reports each with their own see / change permissions; menus, pages, dashboard widgets, report data sources and
the API all follow them), **built-in roles** (IT Asset Manager, Non-IT Asset Manager, Non-IT Asset Viewer, Asset Lifecycle
Manager, Cloud Administrator, Security Analyst), **custom roles** and **user groups**, with rights applied to signed-in users
at their next request; and **log retention and export**: audit and activity logs kept one year by default (90 days to 10
years or for ever), archived before removal, scheduled exports every 3, 6 or 12 months, export a period now, copies to a
backup destination, CSV downloads.

| ID | Test | Result | Detail |
|---|---|---|---|
| T76 | Roles and groups | **PASS** | 7 built-in roles (read-only); custom role through a group: non-IT, dashboard and reports only (12 other areas refused, report sources: nonit_assets); no privilege escalation; role in use cannot be deleted; switching the group to the viewer role applied to the live session without signing out |
| T77 | Log retention and export | **PASS** | defaults 1 year; 30 days refused; export of 30 days (audit + activity) with checksum and manifest; scheduled half-year archive made; audit and activity CSV downloads |
| T55 | API tokens | **PASS** | existing and new tokens with *Export reports* reach report data |
| — | Unit / PostgreSQL tests | **PASS** | report sources need their area's permission; built-in roles and group grants; audit entries removable only by the database owner through the purge function, never the last 90 days, removal recorded with the last hash; archives contain exactly the period with formula-safe CSV and checksums; retention cut-offs and scheduled periods |

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
6ee9a00e99d607dc30ef236c97f52b876ab59852ec2f244e664e5f28629f7858  Zentriq-1.1.1-docs.zip
6add656393deed647f342a2237ac7d8c93f3592d20576a07208564a124130272  Zentriq-1.1.1-msi-offline-kit.zip
f1388d7611673eeaca9d326e2642b0775ec34c5fadc7d76553b54213c713dea8  Zentriq-Setup-x64.exe
86c8efa85808bebde418cf2e0c123cd0c2a7e7e5d6cda0194fdc499e5d6092bd  Zentriq.msi
68c8afebd1ed4b53564a83ba4c4747b746ae3cb64c42a5f6c18dfd3c8bd8bbc9  ZentriqAgent-x64.msi
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
