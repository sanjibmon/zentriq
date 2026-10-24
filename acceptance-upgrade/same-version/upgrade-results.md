| S01 | Install previous release 1.1.1.79 | **PASS** | schema 22 |
| S03 | Enroll previous agent 1.1.1.79 | **PASS** | agent 4cd9cf03-557d-4a9b-a564-5ab23ce437a9 |
| S02 | Upgrade 1.1.1.79 -> 1.1.1.80 in place keeps data, sign-in and trial (30 devices) | **PASS** | schema 23; Programs and Features: one entry 'Zentriq 1.1.1.80' |
| S05 | New features work after the update | **PASS** | activity log 200, software uninstall 400, agent update 400, dashboard overview 200; dashboard layout saved and read back |
| S04 | Agent 1.1.1.79 -> 1.1.1.80 updated in place (no enrollment code) | **PASS** | old agent checked in to the upgraded server; same registration 4cd9cf03-557d-4a9b-a564-5ab23ce437a9 and certificate; console updates now available; one 'Zentriq Agent' entry (1.1.1.80); service running |
