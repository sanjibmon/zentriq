| H01 | Install previous release 1.1.1 | **PASS** | schema 6 |
| H06 | Install v1.1.1-build.29 over it | **PASS** | half-updated (new console pages, old server: /activity 404); 1 'Zentriq' entry in Apps and Features |
| H03 | Enroll previous agent 1.1.1 | **PASS** | agent c499bb8a-b084-412b-8a31-ca842243315f |
| H02 | Upgrade 1.1.1 -> 1.1.1.143 in place keeps data, sign-in and trial (30 devices) | **PASS** | schema 46; Programs and Features: one entry 'Zentriq 1.1.1.143' |
| H05 | New features work after the update | **PASS** | activity log 200, software uninstall 400, agent update 400, dashboard overview 200; dashboard layout saved and read back |
| H04 | Agent 1.1.1 -> 1.1.1.143 updated in place (no enrollment code) | **PASS** | old agent checked in to the upgraded server; same registration c499bb8a-b084-412b-8a31-ca842243315f and certificate; console updates now available; one 'Zentriq Agent' entry (1.1.1.143); service running |
