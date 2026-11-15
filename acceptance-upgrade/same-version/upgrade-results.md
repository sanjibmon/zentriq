| S01 | Install previous release 1.1.1.145 | **PASS** | schema 47 |
| S03 | Enroll previous agent 1.1.1.145 | **PASS** | agent 0a838a8f-db8b-40bd-98cb-eedf07999a95 |
| S02 | Upgrade 1.1.1.145 -> 1.1.1.146 in place keeps data, sign-in and trial (30 devices) | **PASS** | schema 48; Programs and Features: one entry 'Zentriq 1.1.1.146' |
| S05 | New features work after the update | **PASS** | activity log 200, software uninstall 400, agent update 400, dashboard overview 200; dashboard layout saved and read back |
| S04 | Agent 1.1.1.145 -> 1.1.1.146 updated in place (no enrollment code) | **PASS** | old agent checked in to the upgraded server; same registration 0a838a8f-db8b-40bd-98cb-eedf07999a95 and certificate; console updates now available; one 'Zentriq Agent' entry (1.1.1.146); service running |
