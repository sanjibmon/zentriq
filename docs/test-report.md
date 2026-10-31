# Test report

Latest: GitHub Actions run 41205117265, workflow `windows-release`, on a GitHub-hosted **Windows Server 2022 Datacenter
10.0.20348** VM. Release: `v1.1.1-build.99` (pre-release, **unsigned**), package version **1.1.1.99**. Results: unit +
PostgreSQL integration tests **352 PASS, 2 skipped** (ci-quick: 354/354); installer acceptance **71 passed, 0 failed**;
in-place updates PASS (schema 31).

**License server** (separate product, workflow `license-server`): release `license-server-v1.1.0-build.13`,
`Zentriq-LicenseServer-Setup-1.1.13.msi` (SHA256 `ebabe8222cc0570a87373f44274bd7c06320e54932079fbd4483b98179215f68`).
One Setup for Windows Server instead of the zip and command prompt: it installs the server and its own PostgreSQL as two
Windows services and opens a browser setup wizard (owner, signing key created or imported, key backup and product
entry, who can reach the server: this computer only, or HTTPS with Let's Encrypt or your certificate). The key unlocks
by itself after a restart; daily database backups. Tests: 14/14 (Linux, incl. the wizard), Windows Setup test 7/7.

| ID | Test (license server Setup, Windows Server 2022) | Result |
|---|---|---|
| S01 | Silent install: services (server as its own virtual account), loopback-only listeners, portal leads to the wizard, data folder closed | **PASS** |
| S02 | Wizard: owner, key ceremony, product entry, encrypted backup, finish only after the backup is confirmed | **PASS** |
| S03 | Subscription offline file, perpetual online + check-in, customer license file bound online, tampering rejected | **PASS** |
| S04 | Both services stopped and the server started: database starts as dependency, key unlocked without a prompt | **PASS** |
| S05 | Public mode with own certificate: service restarts itself, HTTPS with that certificate, wizard 404 from the network, port taken by another program reported (server keeps running), back to local | **PASS** |
| S06 | Daily and on-demand database backups | **PASS** |
| S07 | Uninstall keeps the data; reinstall continues with the same key and licenses | **PASS** |

New in build 99 (builds 96-98 did not pass acceptance and were not released):

* **IT accessories: add categories.** The category field offers *+ Add category…*; the *Categories* button adds,
  renames and deletes categories (in use: refused). Categories are kept in their own table (migration V0031, 15
  built-in categories plus the ones already used).
* **Agents: no "Tampering blocked" message** on each device; only *Protection paused* is shown.
* **Agent service Stop / Restart with a code.** Stop and Restart in the Services console (and `net stop`, `sc stop`,
  `Stop-Service`) are no longer greyed out. While tamper protection is on, the request is held (*Stopping*) and a code
  dialog opens for each signed-in administrator: with the device's code or the code for all agents, **Restart service**
  restarts it and **Stop service** stops it. Windows does not let a service refuse a stop it has received (an SCM test
  in build 97 showed that a refused stop leaves the service unable to take any request, error 1061), so without a code
  (cancel, wrong code, two minutes) the agent completes the stop and starts again by itself at once, and the attempt is
  reported as tampering.

| ID | Test | Result |
|---|---|---|
| T78 | Stop without a code: held (StopPending), wrong code refused, cancelled → started again at once (new process); code + Restart → restarted (new process); code + Stop → stopped; disabling refused; console pause / resume; device code and code for all | **PASS** |
| T67 | Uninstall protection; stop without a code held and started again | **PASS** |
| T69 | IT accessories incl. built-in and added categories | **PASS** |
| T29, T47, T60, T79 | Revocation, updates and remote support with the new service host | **PASS** |

New in build 95: **license files validated by the license server**. Zentriq sends the customer a signed license file
bound to no server; *Licensing › Import license file* checks the signature, has the Zentriq license server validate it
(license active, newest file, activation free) and only then activates the license, bound to that server. Air-gapped
servers use the same file on the license server's offline page. The license server, its signing code and tool and its
documentation moved out of the customer product (`licensing/`); customer packages carry only customer documentation
(no test report, no code-signing notes).

| ID | Test (license server, Windows local edition) | Result |
|---|---|---|
| L08 | License file for the customer: validated and bound by the server; a reissued file replaces the old one | **PASS** |
| L01-L07 | Setup and key ceremony, start, sign-in, subscription and perpetual licenses, online/offline activation, tamper detection, backup | **PASS** |

New in build 94: **key ceremony** for the production license signing key (generated on the license server's own
machine; ECDSA P-256; encrypted PKCS#8 with AES-256 and PBKDF2-SHA256 600,000 iterations; owner-only file; SHA-256
fingerprint; ceremony record; sign/verify self-test at every start); **Verify file** in the license portal and test
files for license generation; **local edition for Windows** (`Zentriq-LicenseServer-Local-win-x64-1.1.1.zip`:
self-contained server, portable PostgreSQL 17, `zls setup | start | stop | status | backup | key-info`); Zentriq
accepts `http://localhost` as license server address for local testing.

| ID | Test (local license server, Windows PowerShell 5.1) | Result |
|---|---|---|
| L01 | Setup: own PostgreSQL, key ceremony (encrypted key, owner-only ACL, public entry, record), owner account | **PASS** |
| L02 | Start in the background; portal and offline activation page answer | **PASS** |
| L03 | Owner signs in; portal shows the ceremony's key id and fingerprint | **PASS** |
| L04 | Subscription license (3 years): offline file verified with its entitlements | **PASS** |
| L05 | Perpetual license: online activation; support extension arrives with the next check-in | **PASS** |
| L06 | A changed license file is rejected (bad signature) | **PASS** |
| L07 | Database backup and stop | **PASS** |

New in build 93: licensing by **IT devices** (per device, network devices included), **non-IT packs** (250 / 500 / 1,000 /
2,000 / 4,000 / unlimited), **cloud and VM instances** and **remote support technicians**; IT accessories unlimited.
Trial 30 / 50 / 10 / 1; **free tier** 25 / 25 / 5 / none (Standard edition). Subscriptions for 1, 3 or 5 years, perpetual
licenses with yearly (multi-year) support. **Online activation** with a license key, daily check-ins with a 30-day lease,
deactivation; offline activation kept for air-gapped servers. New **Zentriq License Server** (`licensing/`, released as
`Zentriq-LicenseServer-1.1.1.zip`): activation API, offline activation page, license portal.

| ID | Test | Result | Detail |
|---|---|---|---|
| T81 | Licensing plans and online activation | **PASS** | plans and metrics reported; bad key 400, http address 400, unreachable server 502 with a clear message; still Trial |
| T24 | Trial limits | **PASS** | limits: devices 30, nonit_assets 50, cloud_instances 10, technicians 1 |

New in build 92: the product is named **Zentriq Asset Intelligence** (console, installer artwork and package
description); **Re-run** for finished scans (a new scan with the same targets, exclusions, credentials and options);
agent commands on the device: `--checkin-now`, `--collect-now`, `--diagnose`, `--restart`.

| ID | Test | Result | Detail |
|---|---|---|---|
| T80 | Agent check-in and diagnosis commands | **PASS** | `--checkin-now`: Checked in (last check-in updated in the console); `--diagnose`: no problem (server key matches); `--restart`: running again |
| T34 | Scan re-run and history | **PASS** | re-run started a new scan with the same name and targets, the original kept; delete single and by age |

New in build 90: **agent tamper protection for stop and disable** — while protection is on, nobody on the device (local
or domain administrators included) can stop, disable or reconfigure the agent service, change its registry key or its
program folder; changes made behind its back are undone and reported; a watchdog task restarts it after a reboot;
`--stop`, `--disable`, `--pause <code>` with a one-hour code (device code or code for all), `--enable`; console control per
agent (pause / resume protection, restart, disable, one-hour device code). **Automatic agent updates** (optional, off by
default, limit of simultaneous updates).

| ID | Test | Result | Detail |
|---|---|---|---|
| T78 | Agent stop and disable protection | **PASS** | Services, sc.exe and the registry refused; program folder read-only; watchdog task present; wrong code refused and reported (audit + agents list); stop with the device code and `--enable`; disable with the code for all and `--enable`; protection back each time; console pause allowed Stop-Service, resume refused it again |
| T79 | Automatic agent update | **PASS** | off by default; an agent installed at the older version updated itself to 1.1.2.90 without a console request |
| T67 / T47 / T29 | Uninstall protection, console update, revocation | **PASS** | unchanged behaviour |

New in build 88: the *Cloud & Infrastructure › Add connection* dialog is laid out in numbered sections (Platform grouped
into Public cloud and Virtualization, Connection, Sign-in, Scope, Synchronisation) on one aligned two-column grid, checked
for all nine platforms at desktop and narrow widths. No API or database change.

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
8c15c803ff8b3fc15ab82f5b9081dc5fb02db3d02ba519423552bd5f1a45a9ce  Zentriq-1.1.1-docs.zip
5c31f1c8ed049f89524aebbbc2fa5b7715d18e65614dfc5070b1bd90b57680cd  Zentriq-1.1.1-msi-offline-kit.zip
01f8c9708436a15671e38adb404232b2f679f2fffc5a5abaf198175da0589711  Zentriq-Setup-x64.exe
58db92aa1b30856263f83b2c6ade65b128f6d7b84d8733da3e83b9fbbe40dd1a  Zentriq.msi
daee1c325419159263c2cb78f9729eefe2cf3a85c05f5857f54101b1b61b3a76  ZentriqAgent-x64.msi
```

License server (separate release `license-server-v1.0.0-build.3`):

```
08049bd29fe1e8bc9fa12e9fb10c948febf6d477534b458f8ebe9991066fc237  Zentriq-LicenseServer-1.0.0.zip
28b5caf2aecf9667d174793dd724c2b53836c64eee7a8ed0d41257e8c1c270f6  Zentriq-LicenseServer-Local-win-x64-1.0.0.zip
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
