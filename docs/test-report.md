# Test report

Latest: GitHub Actions run 39767245830, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022 Datacenter
10.0.20348** VM. Release: `v1.1.1-build.79` (pre-release, **unsigned**), package version **1.1.1.79**. Results: unit +
PostgreSQL integration tests **248 PASS, 2 skipped** (the live remote support and live SNMP tests run in ci-quick: PASS);
installer acceptance **61 passed, 0 failed**; in-place updates PASS.

New in build 79:
- Standard / Advanced installation with Update / Repair / Remove.
- Standard / Advanced licence editions.
- Rearrangeable columns.
- IPv4 by default in reports.
- Modern remote support session window, with no engine name anywhere a customer can see.

| ID | Test | Result | Detail |
|---|---|---|---|
| T01 | Silent installation with `ZQ_SECTIONS=all` | **PASS** | exit 0 in 1 min |
| T71 | Installation type | **PASS** | trial with 6 sections recorded as Advanced (registry `Sections=all`); Update to Standard: Remote support and Secure & remediate not installed, vulnerabilities API 403, ZentriqRemote disabled, inventory works; Update to Advanced: all sections, remote service running |
| T59 / T60 | Remote support (modern interface, siteStyle 3) | **PASS** | branded server, agent connects, signed-in session opens; install/remove via agent |
| — | Unit tests (EditionTests) | **PASS** | Standard edition covers Overview, Discover & inventory, Insight & admin only; Advanced covers all; extra licensed modules add up; installed sections parsed (standard sections always included); trial limited to the installed sections; expired licence enables nothing |
| — | PostgreSQL tests | **PASS** | report, integration and inventory queries with the IPv4 display address (grouped by device) |

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
a833ad3fc27cbf867b0fd77963fe7878957013a4cc2b9be1f1faeb28c3d2c506  Zentriq-1.1.1-docs.zip
f3ded3e99f80afc30495ef5dbd0b3ba999cc56ffb356d7673d28b81598ec8f8f  Zentriq-1.1.1-msi-offline-kit.zip
4e153e3b9a8794e0cb51a2817eb1121be2f532bd8e74354a8651e52297f003c3  Zentriq-Setup-x64.exe
52f5d1a4cefe7794eabeb0d87c7eff0305fc9d9865470074bc2381748ea05439  Zentriq.msi
7bae70857b1a321e117185a0613325067a55b89b55b5525688737ada3fb619dc  ZentriqAgent-x64.msi
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
