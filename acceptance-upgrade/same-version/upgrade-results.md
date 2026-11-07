| S01 | Install previous release 1.1.1.117 | **PASS** | schema 37 |
| S03 | Enroll previous agent 1.1.1.117 | **PASS** | agent 4b584587-8b7b-40a2-8712-c34505306f56 |
| S02 | Upgrade 1.1.1.117 -> 1.1.1.118 in place keeps data, sign-in and trial (30 devices) | **PASS** | schema 38; Programs and Features: one entry 'Zentriq 1.1.1.118' |
| S05 | New features work after the update | **PASS** | activity log 200, software uninstall 400, agent update 400, dashboard overview 200; dashboard layout saved and read back |
| S04 | Agent 1.1.1.117 -> 1.1.1.118 updated in place (no enrollment code) | **PASS** | old agent checked in to the upgraded server; same registration 4b584587-8b7b-40a2-8712-c34505306f56 and certificate; console updates now available; one 'Zentriq Agent' entry (1.1.1.118); service running |
