| S01 | Install previous release 1.1.1.95 | **PASS** | schema 30 |
| S03 | Enroll previous agent 1.1.1.95 | **PASS** | agent c0bae248-3c64-409a-8700-5866aa515926 |
| S02 | Upgrade 1.1.1.95 -> 1.1.1.99 in place keeps data, sign-in and trial (30 devices) | **PASS** | schema 31; Programs and Features: one entry 'Zentriq 1.1.1.99' |
| S05 | New features work after the update | **PASS** | activity log 200, software uninstall 400, agent update 400, dashboard overview 200; dashboard layout saved and read back |
| S04 | Agent 1.1.1.95 -> 1.1.1.99 updated in place (no enrollment code) | **PASS** | old agent checked in to the upgraded server; same registration c0bae248-3c64-409a-8700-5866aa515926 and certificate; console updates now available; one 'Zentriq Agent' entry (1.1.1.99); service running |
