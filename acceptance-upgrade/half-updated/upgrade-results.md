| H01 | Install previous release 1.1.1 | **PASS** | schema 6 |
| H06 | Install v1.1.1-build.29 over it | **PASS** | half-updated (new console pages, old server: /activity 404); 1 'Zentriq' entry in Apps and Features |
| H03 | Enroll previous agent 1.1.1 | **PASS** | agent d3b0b45b-7f82-45f3-9cb3-faed64c0aa04 |
| H02 | Upgrade 1.1.1 -> 1.1.1.99 in place keeps data, sign-in and trial (30 devices) | **PASS** | schema 31; Programs and Features: one entry 'Zentriq 1.1.1.99' |
| H05 | New features work after the update | **PASS** | activity log 200, software uninstall 400, agent update 400, dashboard overview 200; dashboard layout saved and read back |
| H04 | Agent 1.1.1 -> 1.1.1.99 updated in place (no enrollment code) | **PASS** | old agent checked in to the upgraded server; same registration d3b0b45b-7f82-45f3-9cb3-faed64c0aa04 and certificate; console updates now available; one 'Zentriq Agent' entry (1.1.1.99); service running |
