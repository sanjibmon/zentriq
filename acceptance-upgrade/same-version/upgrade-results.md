| S01 | Install previous release 1.1.1.55 | **PASS** | schema 14 |
| S03 | Enroll previous agent 1.1.1.55 | **PASS** | agent d5613f85-5016-4bce-83d6-61c33a00f8f3 |
| S02 | Upgrade 1.1.1.55 -> 1.1.1.57 in place keeps data, sign-in and trial (30 devices) | **PASS** | schema 15; Programs and Features: one entry 'Zentriq 1.1.1.57' |
| S05 | New features work after the update | **PASS** | activity log 200, software uninstall 400, agent update 400, dashboard overview 200; dashboard layout saved and read back |
| S04 | Agent 1.1.1.55 -> 1.1.1.57 updated in place (no enrollment code) | **PASS** | old agent checked in to the upgraded server; same registration d5613f85-5016-4bce-83d6-61c33a00f8f3 and certificate; console updates now available; one 'Zentriq Agent' entry (1.1.1.57); service running |
