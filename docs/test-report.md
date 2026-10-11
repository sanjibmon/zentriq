# Test report

Source of truth: GitHub Actions run 38111812892 (commit `ad7e8b0`), workflow `windows-release`, on a GitHub-hosted
**Windows Server 2022 Datacenter 10.0.20348** VM (16 GB RAM). Release: `v1.0.0-build.10` (pre-release, unsigned).
Raw logs: branch `ci-logs` of this repository.

## Build and packaging

| Step | Result |
|---|---|
| `build.ps1` (.NET SDK 10.0.401, NuGet audit, warnings-as-errors for vulnerable packages) | PASS |
| `test.ps1` – unit + PostgreSQL 14 integration tests | PASS – total=33 passed=33 failed=0 notExecuted=0 (skipped) |
| `package-windows.ps1` – self-contained publish, prerequisite download with Authenticode + pinned SHA-256 checks, MSI (ICE validation), Burn bundle, checksums, manifest | PASS |
| Code signing | NOT RUN – no signing certificate configured; artifacts are unsigned |

## Release artifacts (SHA-256)

```
a5613061df1cbce922d5eb930930eed51dab2ecf5fb6a997b857dfb4fad4a8fc  Zentriq-1.0.0-docs.zip
e51b979c19e2f23fd259fbe4b551f2dfa43e05a1551884fa44b003c17983cb91  Zentriq-1.0.0-msi-offline-kit.zip
4e39cc31bc18a6c8bc3f441ec52c1a7b34b7a8cab804db0a8cab7ff3789180fb  Zentriq-Setup-x64.exe
6e183bbcb45d03fa2c6f7a2fb06148366eebba8fa337141ee5a0bc2ae355f689  Zentriq.msi
```

Bundled third-party installers (verified signer and pinned hash):
* postgresql-17.11-1-windows-x64.exe — EnterpriseDB Corporation — f104c552d8495a6f20738c2a03f643164bc64b9985363329e314dec24559f0b7
* vc_redist.x64.exe 14.44.35211.0 — Microsoft Corporation — cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b

## Installer acceptance (tests/installer/Invoke-InstallerAcceptance.ps1)

| ID | Test | Result | Detail |
|---|---|---|---|
| T01 | First-time installation (silent, answer file) | **PASS** | exit 0 in 2 min |
| T02 | PostgreSQL provisioned as a Windows service | **PASS** | running, loopback-only |
| T05 | API and worker services running under virtual accounts | **PASS** | ready: {"status":"ready","checks":{"database":"ok","schemaVersion":3,"expectedSchemaVersion":3,"initialized":true,"worker":"ok"}} |
| T03 | Database migrations applied | **PASS** | schema version 3 |
| T06 | Trial initialized for 14 days | **PASS** | expires 10/25/2026 04:38:14 |
| T04 | First administrator can sign in | **PASS** | signed in as admin@ci.zentriq.test |
| T07 | HTTPS certificate is valid and trusted on the server | **PASS** | TLS OK, firewall rule present |
| T08 | zentriqctl verify (end-to-end health) | **PASS** | all checks passed |
| T09 | Service restart does not reset the trial | **PASS** | trial unchanged |
| T10 | Repair keeps data and trial | **PASS** | repaired; trial unchanged; sign-in works |
| T11 | Encrypted backup and deep restore validation | **PASS** | zentriq-20261011-043849-ci.zbak (29 KB) |
| T12 | Tampered/unsigned license is rejected | **PASS** | rejected with 422 |
| T13 | Upgrade to the next release preserves data and trial | **PASS** | now 1.0.1 (database reports 1.0.1); trial unchanged |
| T14 | Logs contain no administrator password or connection-string passwords | **PASS** | 37 log files scanned |
| T15 | Uninstall removes the program but keeps customer data | **PASS** | program removed; data, backups and PostgreSQL kept |
| T16 | Reinstall reuses existing data without resetting the trial | **PASS** | data reused; trial unchanged; sign-in works |
| T17 | Restart of Windows (trial persists across reboot) | **NOT RUN** | CI runners cannot reboot mid-job; covered by T09/T10/T16 persistence checks. Run manually on a VM. |
| T18 | Trial expiry enforcement end-to-end (clock moved past 14 days) | **NOT RUN** | Covered by unit tests (LicenseEvaluator); not run against an installed server. |
| T19 | Valid commercial license activation end-to-end | **NOT RUN** | Requires the vendor private key; signer + verifier covered by unit tests. Run manually with docs/licensing.md. |
| T20 | AD/OU scan in a test domain | **NOT RUN** | No Active Directory domain on CI runners. |
| T21 | Interactive wizard (UI) walk-through | **NOT RUN** | Silent install exercises the same provisioning; UI pages need a manual run on a VM. |

## Not yet verified (from the requirements' acceptance list)

| Item | Status |
|---|---|
| Interactive wizard pages (UI) on a VM | NOT RUN – silent install uses the same provisioning engine; click through once on a clean VM before release |
| Windows reboot persistence | NOT RUN – persistence verified across service restart, repair, upgrade, uninstall/reinstall |
| Trial expiry, workload limit enforcement end-to-end | Unit tests only |
| Valid commercial license activation end-to-end | Unit tests only (needs the production signing key) |
| Manual IP scan, AD/OU scan, durable job recovery end-to-end | NOT RUN (job lease recovery covered by a DB integration test) |
| Dashboard/report persistence | NOT RUN – web console (React UI) not yet built; installer ships a placeholder page |
| Installation with an existing PostgreSQL server | NOT RUN |
