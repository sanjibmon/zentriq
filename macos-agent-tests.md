| Check | Result | Detail |
|---|---|---|
| M01 install the package | **PASS** | macOS 26.7.2, arm64 |
| M02 launchd daemon running | **PASS** | com.zentriq.agent |
| M03 version | **PASS** | 1.1.1.146 |
| M04 signature | **PASS** | Signature=adhoc |
| M05 inventory | **PASS** | macOS 26.7.2, Apple Apple Virtual Machine 1, Apple M1 (Virtual), 7 GB, 118 apps (116 with version), FileVault False, firewall False, SIP False, admins ['runner'], errors [] (7s) |
| M06 status | **PASS** | Zentriq Agent 1.1.1.146 (macos-arm64, installed as pkg) Status:        pending  |
| M07 enrollment against an unreachable server fails cleanly | **PASS** | The enrollment code must have 20 letters and digits (copy it from Agents > Enrollment codes). |
| M08 service log | **PASS** | ﻿2026-11-15 16:26:51Z INFO  Zentriq Agent 1.1.1.146 started (macos-arm64). |
| M09 reinstall (upgrade path) keeps the daemon running | **PASS** |  |
| M10 uninstall | **PASS** | The Zentriq Agent was removed. |

**10 passed, 0 failed**
