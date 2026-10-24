| H01 | Install previous release 1.1.1 | **PASS** | schema 6 |
| H06 | Install v1.1.1-build.29 over it | **PASS** | half-updated (new console pages, old server: /activity 404); 1 'Zentriq' entry in Apps and Features |
| H03 | Enroll previous agent 1.1.1 | **PASS** | agent 919f2ec8-e354-4d6f-ad85-cc6a406b067d |
| H02 | Upgrade 1.1.1 -> 1.1.1.80 in place keeps data, sign-in and trial (30 devices) | **PASS** | schema 23; Programs and Features: one entry 'Zentriq 1.1.1.80' |
| H05 | New features work after the update | **PASS** | activity log 200, software uninstall 400, agent update 400, dashboard overview 200; dashboard layout saved and read back |
| H04 | Agent 1.1.1 -> 1.1.1.80 updated in place (no enrollment code) | **PASS** | old agent checked in to the upgraded server; same registration 919f2ec8-e354-4d6f-ad85-cc6a406b067d and certificate; console updates now available; one 'Zentriq Agent' entry (1.1.1.80); service running |
