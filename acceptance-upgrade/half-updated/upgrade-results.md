| H01 | Install previous release 1.1.1 | **PASS** | schema 6 |
| H06 | Install v1.1.1-build.29 over it | **PASS** | half-updated (new console pages, old server: /activity 404); 1 'Zentriq' entry in Apps and Features |
| H03 | Enroll previous agent 1.1.1 | **PASS** | agent 8bc8bba3-da57-4499-8663-049aa8832061 |
| H02 | Upgrade 1.1.1 -> 1.1.1.57 in place keeps data, sign-in and trial (30 devices) | **PASS** | schema 15; Programs and Features: one entry 'Zentriq 1.1.1.57' |
| H05 | New features work after the update | **PASS** | activity log 200, software uninstall 400, agent update 400, dashboard overview 200; dashboard layout saved and read back |
| H04 | Agent 1.1.1 -> 1.1.1.57 updated in place (no enrollment code) | **PASS** | old agent checked in to the upgraded server; same registration 8bc8bba3-da57-4499-8663-049aa8832061 and certificate; console updates now available; one 'Zentriq Agent' entry (1.1.1.57); service running |
