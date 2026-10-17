# Test report

Latest: GitHub Actions run 39012398399 (commit `fbeb474`), workflow `windows-release`, on a GitHub-hosted
**Windows Server 2022 Datacenter 10.0.20348** VM. Release: `v1.1.1-build.53` (pre-release, **unsigned**), package
version **1.1.1.53**. Results: unit + PostgreSQL integration tests **152/152 PASS**; installer acceptance
**51 passed, 0 failed, 0 blocked, 10 not run**; in-place updates from published builds and from 1.0.2 PASS.

New in build 53 (nothing tied to a fixed server address):

| ID | Test | Result | Detail |
|---|---|---|---|
| T61 | Server certificate follows the server | **PASS** | Zentriq Local CA trusted on the server and downloadable without sign-in; certificate covers every IPv4 address (172.26.48.1, 10.1.0.157), each validated by IP without skipping checks; after a host-name change the API re-issued the certificate by itself with the same key, and remote support switched to it |
| T59 / T60 / T47 / T13 | Remote support, agent update, upgrade | **PASS** | with the CA-issued certificate; agents kept connecting (same key) |
| — | Unit tests | **PASS** | CA issue/chain, coverage of names and addresses, re-issue keeps the agent key pin, PFX round trip and PEM export for remote support |
| — | Build 53 first attempt | FAIL → fixed | a certificate issued right after the CA was created started before the CA's validity |

New in build 52 (remote session opening):

| ID | Test | Result | Detail |
|---|---|---|---|
| T59 | Remote support (extended) | **PASS** | port 8446 serves the same certificate as the console; session and installer checks as before |
| — | Live MeshCentral test, second start | **PASS** | engine presents the console certificate, stays in WAN mode, signed-in link works; session address follows the browser's host (e.g. 192.168.1.8) |
| — | Reported issue | fixed | session tab opened on the server's computer name with a separate certificate; browsers with HSTS for that name offered no way to continue |

New in build 51 (Remote Management › Remote Support, MeshCentral 1.2.5 engine with Zentriq branding):

| ID | Test | Result | Detail |
|---|---|---|---|
| T59 | Remote support: engine running, branded installer, remote agent connects, signed-in session | **PASS** | ZentriqRemote service and health.ashx OK; installer 3.5 MB, version info "Zentriq Remote Support Agent"; the runner came online in Remote Support; the desktop session link opened the signed-in device view; a forged link was refused; session in history |
| T60 | Remote agent removed and reinstalled through the Zentriq Agent from the console | **PASS** | both actions succeeded and appear in the Activity log; device online again |
| — | Live test against a real MeshCentral (ci-quick) | **PASS** | control channel; group, user and consent management; sign-in link; agent installer; agent settings point to `wss://host:port/agent.ashx` (WAN mode, also for dotless host names); sync |
| — | Unit / DB tests | **PASS** | sign-in cookie format, server config, rights/consent flags, certificate name; NUL characters removed from inventory |
| — | Builds 49–50 | FAIL → fixed | MeshCentral 1.2.6 not on npm (pinned 1.2.5); packaging module check path; engine ran in LAN-only mode for a dotless host name (agents could not find it); the remote agent's uninstall entry contains a NUL character, which broke inventory upload (now stripped) |
| — | New console screens (Remote Support devices, session history, deployment & settings; device page buttons; navigation) | checked | rendered from the built console against a mock API |

New in build 48 (departments, change history, console options):

| ID | Test | Result | Detail |
|---|---|---|---|
| T56 | Departments: create, assign, filter, dashboard, reports, rename, delete | **PASS** | duplicate name refused; deleting leaves devices unassigned |
| T57 | Change history on the runner: software installed → updated → uninstalled | **PASS** | 1.0.0 → 2.0.0 → removed, all recorded; shown device-wise; change report rows |
| T58 | Company logo | **PASS** | PNG stored and served without sign-in; SVG refused; removal |
| — | Unit / DB tests | **PASS** | hardware/software diff (memory, disks, CPU, BIOS, replaced board, adapters, KBs, OS upgrade), false-change guards, department report columns |
| — | Build 47 | FAIL → fixed | acceptance T53/T57 script issues (strict mode; collect-now fallback), not product defects |

New in build 46 (Security Configuration, Integrations & Automation):

| ID | Test | Result | Detail |
|---|---|---|---|
| T53 | Security configuration: settings collected (agentless WinRM), 30 checks, score, baseline, exception, report | **PASS** | runner: score 60; 12 passed, 11 failed (SC-05, SC-09, SC-11, SC-16, SC-19, SC-21, SC-22, SC-25, SC-27, SC-28, SC-29 — a default, unhardened Azure image), 1 unknown; server-only baseline with SC-07 = 7 days; out-of-range parameter refused; exception shows as "Exception"; security report and built-in reports run |
| T54 | Integrations: webhook + syslog channels, test messages, alert rule, failure logging | **PASS** | HMAC-SHA256 signature verified by the receiver; CEF line `<134>1 … Zentriq - test - CEF:0\|Zentriq\|Zentriq\|1.1.1\|test\|…`; rule delivered `asset.discovered` to both channels within a minute; stored channel credentials never returned by the API; http:// Slack URL refused; unreachable channel reports the connection error |
| T55 | API tokens | **PASS** | report data as CSV and JSON with a bearer token; 403 for a permission the token lacks; POST refused (401); last use recorded; revoked token → 401; write permissions cannot be given to a token |
| — | Unit / DB tests | **PASS** | 30-check catalogue, hardened vs. weak devices, unknown and scope rules, Windows lifecycle (Win10 22H2, Win11 23H2 Pro/Enterprise, LTSC, Server 2012 R2–2025), scoring, baselines, exceptions, registry reading (policy first, Windows defaults); webhook signature, Slack/Teams/CEF/ServiceNow/Jira formats; end-to-end evaluation on PostgreSQL; alert rule → signed webhook to a local listener, retries; every built-in report runs |
| — | New console screens (security overview, checks, check detail, baseline editor, devices, exceptions; integrations overview, channels, channel dialog, alert rule, delivery log, API access) | checked | rendered from the built console against a mock API |
| — | ServiceNow, Jira, Slack and Teams against the real services | **NOT RUN** | no accounts on CI; request formats covered by unit tests — use **Send test** after configuring |

New in build 45 (Reports & Analytics, Vulnerability Management):

| ID | Test | Result | Detail |
|---|---|---|---|
| T48 | Reports: run, group, export (XLSX/CSV), injection-safe filters, save, schedule and deliver | **PASS** | 9 data sources, 22 built-in reports; delivery recorded as failed only because SMTP is not configured on the runner (file kept) |
| T49 | Vulnerabilities: NVD file import, CPE mapping, evaluation, risk acceptance | **PASS** | 3 findings; P2, risk 69; acceptance and re-open |
| T50 | Windows vulnerabilities from a Microsoft (CVRF) file, workflow, remediation plan | **PASS** | matched against the runner's real build 10.0.20348.5622; KB and fixed build; Microsoft "exploited" → P1; start / ticket / note with history; survives re-evaluation; plan line "Install the latest cumulative update for Windows Server 2022 (KB… or later)" |
| T51 | Live Microsoft data from api.msrc.microsoft.com | **PASS** | 3 months loaded online: 2,063 Windows CVEs with fixed builds; the (not fully patched) runner shows 978 real open Windows findings, e.g. fixed by KB5126655 (build 10.0.20348.5761 vs. runner 5622) |
| T52 | Report analytics | **PASS** | distinct count / average / maximum per group, month buckets, software titles / discovery / audit data sources, PDF (32 pages, header on every page, page numbers; checked by rendering), saved report on the dashboard, invalid report widget refused |
| T47 | Agent update from the console | **PASS** | 1.1.1.45 → 1.1.2.45 in place |
| — | New console screens (vulnerability overview, findings, finding history, remediation plan, CVE detail with Windows updates, report designer with calculations, dashboard with report and vulnerability widgets) | checked | rendered from the built console against a mock API |
| — | Windows vulnerability matching on Windows 10/11 clients, Server 2016/2019 and Server Core | **NOT RUN** | product mapping covered by unit tests; only Server 2022 runs in CI |

Build 37:

| ID | Test | Result | Detail |
|---|---|---|---|
| H01 | Install published `v1.1.1-build.27` | **PASS** | schema 6 |
| H06 | Install published `v1.1.1-build.29` over it | **PASS** | reproduces the half-updated server: new console pages, old server (`/activity` 404) |
| H03 | Enroll the build-27 agent | **PASS** | |
| H02 | Run build 37 Setup over the half-updated server | **PASS** | updated in place to 1.1.1.37, schema 8, data/sign-in/trial kept, one 'Zentriq' entry |
| H05 | New features answer after the update | **PASS** | Activity log, software uninstall, agent update, dashboard overview; a customised dashboard layout is saved and read back |
| H04 | Build-27 agent: new MSI installed over it once, no enrollment code | **PASS** | same registration and certificate; now reports the self-update capability |
| S01–S05 | Same as above from published build 33 | **PASS** | |
| U01, U02, U05 | Upgrade from published 1.0.2 | **PASS** | |
| T47 | Agent update from the console | **PASS** | 1.1.1.37 → 1.1.2.37: agent downloaded the package over mTLS, verified SHA-256, updated in place; same registration and certificate; Activity log "Updated to 1.1.2.37"; an up-to-date agent is not queued again |
| — | Dashboard layout save on an existing dashboard | fixed | returned 409 before (found by H05/S05/U05) |
| — | Interactive wizard pages and the Agents/Dashboard screens on a live server | **NOT RUN** | CI installs silently; screens checked in a browser against a mock API |

Build 29: T45 software uninstall through the agent (protected software refused, item removed from inventory, Activity log); T46 dashboard drill-down matches the charts, offline fields. Build 27: T41 VM shown as
virtual machine, IPv4 (not IPv4-mapped IPv6) address, IPv6 display toggle, dashboard overview; T42 collect inventory
now for all assets; T43 service restart through the agent (protected service refused); T44 service restart agentlessly
over WinRM with a stored credential (credential prompt enforced). Earlier: T33–T37 (enrollment code, scan deletion,
MFA, password reset, AD connection). NOT RUN: AD sync against a real domain, reset e-mail delivery, Server 2012 R2
refusal, install on Server 2016, SSH/SNMP devices, agent over the internet, reboot persistence, interactive wizard.
Web console screens (sign-in artwork, dashboard charts, asset list, deploy-agent helper, service dialog) were checked
in a browser against a mock API, not against a live server.
Raw logs: branch `ci-logs` of this repository.

The detailed sections below are from the earlier run 38208536699 (`v1.1.0-build.19`).

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
4d6bc007c1dcac6b00d3a91a6c4e349eed8783536edfa3faebca6a891255aaa1  Zentriq-1.1.0-docs.zip
2462536db1386b63759534ad941a84f198c2e386712ea40f2ce0088de6cca89a  Zentriq-1.1.0-msi-offline-kit.zip
5058560bb93a143f8de38c7877d5470967a77d0d291bb2ad5ae148d3f80efcef  Zentriq-Setup-x64.exe
ff2fd7a906eaea9eacee75416208d9f0ad09c5a73e11489a7cf0ccf09f633ff8  Zentriq.msi
8df2d0e27dbf24d7d29ea4c580c679e3c901fc3a7982442e2c2d29dc38e9febb  ZentriqAgent-x64.msi
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
