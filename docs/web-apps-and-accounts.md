# Web Apps & Accounts

Web Apps & Accounts lists the web (SaaS) applications the organisation uses and the accounts in them, so that
administrators can see shadow IT, remove the accounts of people who left and reclaim inactive paid seats.

Console: *Secure & remediate › Web Apps & Accounts*. Permissions: **webapps.view**, **webapps.manage**. Licensed with
the *Secure* section (module *webapps*).

## Where the web apps come from

| Source | What |
|---|---|
| Devices | At each inventory, Windows devices (Zentriq Agent or WinRM) report the host names in their DNS cache. Names that belong to a known web app (catalogue of about 85 apps, or the domains you give your own apps) are kept per app, device and signed-in user; **all other names are discarded**. A catalogue app seen for the first time is added *Under Review*. Turn it off under *Software Licenses › Settings*. |
| Microsoft Entra ID | With a Microsoft 365 connection (*Software Licenses › Cloud Licences*): single sign-on applications and the users assigned to them (directly or through groups), and Microsoft 365 itself with an account per licensed user. |
| Catalogue | *Web Apps › From Catalogue*: Microsoft 365, Google Workspace, Slack, Zoom, Salesforce, Atlassian, GitHub, Adobe, Canva, Figma, Notion, HubSpot, Zendesk, ServiceNow, Workday, AI assistants (ChatGPT, Claude, Gemini, Copilot) and others. |
| By hand | *Add Web App* with its domains, owner, department, cost per user, renewal date. |

Each app has a **status**: *Sanctioned*, *Under Review*, *Unsanctioned* or *Blocked*.

## Accounts

Accounts come from the Microsoft 365 / Entra sync, from the **CSV user list** an app's admin console exports
(*Import CSV*: columns user or e-mail, name, role, status, last login, plan, cost, department in any order; *The
file is the full list* marks accounts that are not in it as removed) or are added by hand. For every account Zentriq
shows the role (owner, administrator, user, guest, service), the plan, the cost, the last activity (sign-in in the
app, Entra sign-in, use seen on a device) and:

* **Orphaned** — the account is active but the person has left (*Asset Lifecycle › People*, status *Left*) or is
  disabled in Microsoft Entra ID;
* **Inactive** — no activity for 90 days (Settings).

## Alerts

| Alert | Severity |
|---|---|
| Unsanctioned web app in use (last 7 days) | Medium; blocked: high |
| Account of a person who left / disabled still active | High for owners and administrators, otherwise medium |
| Inactive accounts in an app (with the cost to reclaim) | Low |

They appear on the dashboard highlights, in the bell for users who can see Web Apps & Accounts, in the e-mail digest
and as the integration event *webapp.risk*. A web app can be linked to a **software product** (Software Licenses) so
that its accounts count as the licences in use, and its renewal is tracked there.

## API

`GET /api/v1/webapps/summary`, `GET/POST /webapps`, `GET/PUT/DELETE /webapps/{id}`, `POST /webapps/status`,
`GET /webapps/catalog`, `POST /webapps/from-catalog`, `GET /webapps/accounts?appId=&filter=`, `POST /webapps/{id}/accounts`,
`PUT/DELETE /webapps/accounts/{id}`, `POST /webapps/{id}/accounts/import`, `GET /webapps/alerts`.

## Tests

Unit tests (catalogue, host matching, CSV exports), PostgreSQL tests (usage kept only for known apps, unsanctioned
use, orphaned and inactive accounts, discovery off) and the Windows acceptance test T87. First released in build
1.1.1.111.
