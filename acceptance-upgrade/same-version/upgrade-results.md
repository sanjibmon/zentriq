| S01 | Install previous release 1.1.1.142 | **PASS** | schema 46 |
| S03 | Enroll previous agent 1.1.1.142 | **PASS** | agent 1c699b4f-db15-43bd-90b5-74ac1256bdbf |
| S02 | Upgrade 1.1.1.142 -> 1.1.1.143 in place keeps data, sign-in and trial (30 devices) | **PASS** | schema 46; Programs and Features: one entry 'Zentriq 1.1.1.143' |
| S05 | New features work after the update | **PASS** | activity log 200, software uninstall 400, agent update 400, dashboard overview 200; dashboard layout saved and read back |
| S04 | Agent 1.1.1.142 -> 1.1.1.143 updated in place (no enrollment code) | **PASS** | old agent checked in to the upgraded server; same registration 1c699b4f-db15-43bd-90b5-74ac1256bdbf and certificate; console updates now available; one 'Zentriq Agent' entry (1.1.1.143); service running |
