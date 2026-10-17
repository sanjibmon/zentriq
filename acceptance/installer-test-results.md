# Zentriq installer acceptance results

Environment: Microsoft Windows Server 2022 Datacenter 10.0.20348 (runnervmgnbhw), 2026-10-17T18:48:28.9735648Z

| ID | Test | Result | Detail |
|---|---|---|---|
| T01 | First-time installation (silent, answer file) | **PASS** | exit 0 in 1 min |
| T02 | PostgreSQL provisioned as a Windows service | **PASS** | running, loopback-only |
| T32 | Leftover data folder from an earlier failed attempt is set aside, not fatal | **PASS** | moved to postgresql.incomplete-20261017-182657 |
| T05 | API and worker services running under virtual accounts | **PASS** | ready: {"status":"ready","checks":{"database":"ok","schemaVersion":15,"expectedSchemaVersion":15,"initialized":true,"worker":"ok"}} |
| T03 | Database migrations applied | **PASS** | schema version 15 |
| T06 | Trial initialized for 14 days | **PASS** | expires 10/31/2026 18:27:55 |
| T04 | First administrator can sign in | **PASS** | signed in as admin@ci.zentriq.test |
| T07 | HTTPS certificate is valid and trusted on the server | **PASS** | TLS OK, firewall rule present |
| T22 | Start menu and Desktop shortcuts use the Zentriq icon | **PASS** | Start menu + Desktop shortcuts with zentriq.ico |
| T23 | Web console sign-in page is served (not the placeholder) | **PASS** | index + /assets/index-Y0fDtp7I.js (562 KB) |
| T08 | zentriqctl verify (end-to-end health) | **PASS** | all checks passed |
| T09 | Service restart does not reset the trial | **PASS** | trial unchanged |
| T10 | Repair keeps data and trial | **PASS** | repaired; trial unchanged; sign-in works |
| T11 | Encrypted backup and deep restore validation | **PASS** | zentriq-20261017-182846-ci.zbak (51 KB) |
| T12 | Tampered/unsigned license is rejected | **PASS** | rejected with 422 |
| T24 | Trial: every module enabled, 30 devices in total | **PASS** | limits: {"devices":30} |
| T25 | Company profile can be edited and read back | **PASS** | profile saved (Zentriq CI Private Limited) |
| T26 | Agent: enrollment code, MSI download from the console, silent install, inventory received | **PASS** | runnervmgnbhw: 73 applications, 16 GB RAM, health online; MSI signature: NotSigned |
| T27 | Agent port serves only the agent API and requires a client certificate | **PASS** | / 404, /api/v1 404, no cert 401, console port 404, firewall rule present |
| T28 | Agentless WinRM inventory with a stored credential; merges with the agent record | **PASS** | 172.22.160.1 via WinRM: 73 applications; assets 1 -> 1; Inventoried runnervmgnbhw over WinRM with credential 'ci-local-admin' (73 software items). |
| T41 | Asset type and address: VM recorded as virtual machine, IPv4 not IPv4-mapped IPv6, logged-on user present | **PASS** | ip 172.22.160.1; type virtual; user 'runnervmgnbhw\runneradmin'; ipv6 toggle works; dashboard: 1 assets |
| T42 | Collect inventory now for all assets | **PASS** | agents 1, agentless 0: 1 agent(s) will send inventory within about 2 minutes. |
| T43 | Restart a Windows service through the Zentriq Agent | **PASS** | W32Time restarted by the agent (Service restarted.); protected service refused |
| T45 | Uninstall software through the Zentriq Agent (single device), recorded in the Activity log | **PASS** | uninstalled via agent: 'CI Dummy App 1.0' was uninstalled.; protected software refused; removed from inventory |
| T46 | Dashboard drill-down filters and offline timeline on assets | **PASS** | drill-down by type and cores matches the charts; offlineDays 0, online True |
| T29 | Agent revocation is enforced and uninstall removes the agent | **PASS** | revoked agent refused; uninstalled cleanly |
| T44 | Restart a Windows service agentlessly (WinRM, stored credential) on a device without an agent | **PASS** | credential prompt enforced; W32Time restarted over WinRM: Service restarted. (runnervmgnbhw via WinRM, account runnervmgnbhw\zqscan) |
| T33 | Enrollment code can be shown again later; revoked codes cannot | **PASS** | code shown again; revoked code refused (410); list does not expose codes |
| T34 | Scan history can be deleted (single and by age); assets are kept | **PASS** | single delete 204, then 404; bulk delete ok (0); assets kept (1) |
| T35 | Authenticator-app MFA: QR enrollment, sign-in requires the code, disable | **PASS** | QR SVG returned; sign-in refused without code, accepted with TOTP; MFA turned off again |
| T36 | Forgot password: no account disclosure; zentriqctl reset-password works | **PASS** | options=false, forgot 503 without SMTP, bogus token 400, zentriqctl reset-password then sign-in OK |
| T37 | Active Directory connection: validation and a clear error when no DC answers | **PASS** | invalid DN rejected; no DC: Cannot reach the domain controller at 127.0.0.1:636 (server down, wrong address, firewall, or no LDAPS certificate on the DC / certificate not trusted by this server).; password not returned |
| T48 | Reports: run, group, export (XLSX/CSV), injection-safe filters, save, schedule and deliver | **PASS** | 11 datasets, 29 reports; xlsx 2529 bytes; delivery 'failed': E-mail (SMTP) is not configured in System Settings › E-mail; the file is kept in the run history. |
| T49 | Vulnerability management: NVD file import, CPE mapping, evaluation, risk acceptance | **PASS** | import ok; 3 product(s) mapped; 3 finding(s); P2 risk 69; acceptance and re-open OK |
| T50 | Windows vulnerabilities: Microsoft file import, build matching, KB, workflow (in progress, ticket, note, history), remediation plan | **PASS** | 1 Windows finding(s) on runnervmgnbhw (10.0.20348.5622); P1 risk 65; workflow + history (3 entries); plan: Install the latest cumulative update for Windows Server 2022 (KB5999999 or later): build 10.0.20348.99999 or higher |
| T51 | Live Microsoft data: 'Update now' loads Windows fixed builds from api.msrc.microsoft.com and evaluates this server | **PASS** | 4 monthly update(s) loaded (2 of the last 3 downloaded now): 2057 Windows CVE(s) with fixed builds. This runner: 978 open Windows finding(s); e.g. CVE-2026-96951 fixed by KB5126655 (build 10.0.20348.5761, runner 10.0.20348.5622). |
| T52 | Report analytics: calculations, date grouping, new data sources, PDF, saved report on the dashboard | **PASS** | aggregates and month buckets OK; 3 new data sources; PDF 547939 bytes; report widget saved |
| T53 | Security configuration: Windows settings collected, 30 checks evaluated, score, baseline, exception, report | **PASS** | runnervmgnbhw: score 60; 12 passed, 11 failed (SC-11, SC-05, SC-09, SC-16, SC-19, SC-21, SC-22, SC-25, SC-28, SC-27, SC-29), 1 unknown; baseline + exception + report OK |
| T54 | Integrations: signed webhook and syslog (CEF) channels, test messages, alert rule delivers an event, failures logged | **PASS** | webhook HMAC verified; CEF syslog: <134>1 2026-10-17T18:35:57.321Z runnervmgnbhw Zentriq - test - CEF:0/Zentriq/Zentriq/1.1.1…; rule delivered asset.discovered to 2 channels; failure logged: No connection could be made because the target machine actively refused it. |
| T55 | API tokens: read-only bearer access to report data, permissions limited, revocation | **PASS** | CSV 243 bytes and 1 JSON row(s) with the token; 403 without permission; POST refused; revoked -> 401 |
| T56 | Departments: create, assign assets, filter, dashboard and reports, rename, delete | **PASS** | 1 asset(s) assigned, filtered, charted and reported; rename and delete OK; duplicate name refused |
| T57 | Change history: software installed, updated and uninstalled on this server is recorded and shown device-wise | **PASS** | runnervmgnbhw: installed 1.0.0 -> updated to 2.0.0 -> uninstalled, all recorded; device-wise summary 4 change(s); report 4 row(s); hardware changes during the test: 0; collected by: collect-now |
| T58 | Company logo: upload (PNG), shown without sign-in, unsafe types refused, removal | **PASS** | PNG stored and served anonymously (70 bytes); SVG refused; removed |
| T59 | Remote support: branded server running, installer, remote agent connects, technician session opens signed in | **PASS** | engine healthy, same certificate as the console (23E4339F...); branded installer 3.5 MB ('Zentriq Remote Support Agent', exit 0); runnervmgnbhw online as Microsoft Windows Server 2022 Datacenter - 21H2/20348; signed-in desktop link opened; control channel by IP 172.22.160.1 accepted, foreign origin refused; forged link refused |
| T60 | Remote support via the Zentriq agent: remove and install the remote agent from the console | **PASS** | removed and reinstalled through the Zentriq agent; runnervmgnbhw online again; 2 successful remote-support action(s) in the Activity log |
| T61 | Server certificate follows the server: local CA, all IP addresses, automatic re-issue with the same key, remote support follows | **PASS** | CA AFE1AFDA... trusted; covers 172.22.160.1, 10.1.1.60 (validated by IP); new name -> re-issued 61BA041E... with the same key in place; remote support switched to it |
| T62 | Devices without an agent: recognised from their web interface; add, device type, duplicates merged (newest kept), delete | **PASS** | web page on 8080 recognised (Recognised as Printer (HP HP LaserJet Pro M404dn)); inventoried computer kept its type (virtual); added mfp, duplicate merged keeping 10.250.0.10; type manual -> automatic; deleted 2 record(s) |
| T13 | Upgrade to the next release preserves data and trial | **PASS** | now 1.1.2.57 (database reports 1.1.2); trial unchanged |
| T47 | Agent update from the console: agent downloads the package over mTLS, verifies it and updates in place | **PASS** | 1.1.1.57 -> 1.1.2.57 in place (same registration and certificate, one Apps entry); Activity log: Updated to 1.1.2.57. |
| T14 | Logs contain no administrator password or connection-string passwords | **PASS** | 64 log files scanned |
| T15 | Uninstall removes the program but keeps customer data | **PASS** | program removed; data, backups and PostgreSQL kept |
| T16 | Reinstall reuses existing data without resetting the trial | **PASS** | data reused; trial unchanged; sign-in works |
| T17 | Restart of Windows (trial persists across reboot) | **NOT RUN** | CI runners cannot reboot mid-job; covered by T09/T10/T16 persistence checks. Run manually on a VM. |
| T18 | Trial expiry enforcement end-to-end (clock moved past 14 days) | **NOT RUN** | Covered by unit tests (LicenseEvaluator); not run against an installed server. |
| T19 | Valid commercial license activation end-to-end | **NOT RUN** | Requires the vendor private key; signer + verifier covered by unit tests. Run manually with docs/licensing.md. |
| T20 | AD/OU scan and directory sync (computers, users, groups, OUs) in a test domain | **NOT RUN** | No Active Directory domain on CI runners. T37 covers validation and error handling only. |
| T38 | Password reset by e-mail (SMTP delivery and link) | **NOT RUN** | No mail server on CI runners; T36 covers the no-SMTP path and zentriqctl reset-password. |
| T39 | Setup refuses Windows Server 2012 R2 and older | **NOT RUN** | CI has only Windows Server 2022; the bundle/MSI conditions need a manual run on an old VM. |
| T40 | Installation on Windows Server 2016 | **NOT RUN** | CI has only Windows Server 2022. Run manually on a Server 2016 VM. |
| T30 | Agentless SSH (Linux) and SNMP collection against real devices | **NOT RUN** | No Linux/SNMP targets on the Windows CI runner; parsers covered by unit tests. Run manually. |
| T31 | Agent over the internet through a firewall/reverse proxy | **NOT RUN** | CI runs agent and server on one machine. Run manually with a published port 8444. |
| T21 | Interactive wizard (UI) walk-through | **NOT RUN** | Silent install exercises the same provisioning; UI pages need a manual run on a VM. |
