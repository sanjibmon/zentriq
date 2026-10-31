# Local license server (Windows package)

| ID | Test | Result | Detail |
|---|---|---|---|
| L01 | Setup: own PostgreSQL, key ceremony (encrypted key, public entry, record), owner account | **PASS** | key zentriq-test-local, SHA256:FA:28:D9:0B:6F:9F:17:26:47:3A:B4:4C:ED:3E:96:E7:D2:5F:EF:53:61:85:92:38:4D:82:F9:43:51:01:EE:42; private key encrypted, owner-only ACL (NT AUTHORITY\SYSTEM, runnervmnjgfq\runneradmin) |
| L02 | Start in the background; portal and offline activation page answer | **PASS** | health 200, / and /admin served |
| L03 | Owner signs in; the portal shows the same key id and fingerprint as the ceremony record | **PASS** | signed in; zentriq-test-local SHA256:FA:28:D9:0B:6F:9F:17:26:47:3A:B4:4C:ED:3E:96:E7:D2:5F:EF:53:61:85:92:38:4D:82:F9:43:51:01:EE:42 |
| L04 | Subscription license (3 years): key shown once, offline test file, verified with the entitlements | **PASS** | ZQ-2026-AC0P3Y: zentriq-license-ZQ-2026-AC0P3Y.zlic verified; ends 2029-10-31 |
| L05 | Perpetual license: online activation, support extension arrives with the next check-in | **PASS** | ZQ-2026-NT5850: activated online, support 2027-10-31 -> 2029-10-31 via check-in; usage 15 recorded |
| L06 | A changed license file is rejected | **PASS** | rejected: The license signature is invalid. The file may have been modified. |
| L07 | Database backup and stop | **PASS** | backup written; server and PostgreSQL stopped |
