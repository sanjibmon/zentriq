# Software License Management

Software License Management (software asset management) compares the software licences the organisation owns with
what is installed and used, and tells administrators when they differ or when a licence ends:

* **Software products** — the software you license, and the names it installs under (wildcards, exclusions,
  publisher). *Discover* suggests the commercial software found on your devices (about 40 well-known products:
  Microsoft Office, Visio, Project, Visual Studio, Adobe Acrobat and Creative Cloud apps, Autodesk, SOLIDWORKS,
  MATLAB, JetBrains, Tableau, TeamViewer and others) and lists the most installed programs that are not tracked.
* **Licences** — perpetual, subscription, volume, OEM, enterprise agreement, trial, freeware and open source; counted
  per device, per user, per core, concurrent users or site-wide; quantity or unlimited; edition, version and downgrade
  rights; vendor, reseller, PO, invoice and contract numbers; purchase, start and end dates, support / maintenance end,
  automatic renewal and billing period; unit and total cost and currency; department and owner; the licence key
  (stored encrypted, shown as its last five characters; *Show* is audited).
* **Allocations** — a licence given to devices or users.
* **Cloud licences** — Microsoft 365 / Entra ID: licences bought, assigned and available per licence type, renewal and
  expiry dates, trials, subscriptions in their grace period or suspended, users with their licences, disabled or
  inactive users who still hold one (reclaim).
* **Windows & Office activation** — every Windows device reports whether Windows and Office are activated (channel:
  Retail, OEM, Volume MAK / KMS, subscription), the last five characters of the key and the OEM licence in its
  firmware. A full product key is never read.
* **Alerts** — discrepancies and dates, in the console, the bell, an e-mail digest and the integration channels.

Console: *Secure & remediate › Software Licenses*. Permissions: **licenses.view**, **licenses.manage**. Licensed with
the *Secure* section (module *licenses*).

## Licence position

For every product, what is **in use** is counted by its metric and compared with the licences **owned** (the sum of
the active licences that have not ended; a licence without a quantity, a site licence, freeware and open source are
unlimited):

| Metric | In use |
|---|---|
| Per device | Devices on which it is installed |
| Per user | Users signed in on those devices (a device without a user counts once) |
| Per core | Processor cores of those devices |
| Concurrent | Not measured (installations are listed) |
| Cloud subscription | Licences assigned in Microsoft 365, or the active accounts of its web app |

| Position | Meaning |
|---|---|
| Compliant | Enough licences |
| Under-licensed | More in use than owned — with the number short and the cost to close the gap (average unit cost) |
| Unlicensed | In use, no valid licence (none recorded, or all ended) |
| Over-licensed | More than 25 % unused (Settings) — with the possible saving |
| Not measured / No licence needed / Not in use | Concurrent licences; free software; nothing installed |

## Expiry and alerts

The worker evaluates everything every 15 minutes (and *Evaluate Now*, renewals and imports do it at once):

| Alert | Severity |
|---|---|
| Under-licensed product | High when 10 or more, or 20 % or more, short; otherwise medium |
| Unlicensed product | Medium |
| Licence ends within its warning days (30 by default, per licence) | High within 7 days, medium before; low when it renews automatically |
| Licence ended | High |
| Support / maintenance ending or ended | Low / medium |
| Microsoft 365: more assigned than bought; subscription in its grace period, suspended, trial ending | High |
| Windows or Office not activated / not genuine | Medium |
| Over-licensed (possible saving) | Information |

A new alert is announced once: in the **bell** for users who can see Software Licenses, in one **e-mail digest** to
the recipients in *Settings* (uses the SMTP settings), and as the integration events *license.compliance*,
*license.expiring* and *license.not_genuine* (Teams, Slack, e-mail, webhook, ServiceNow, Jira — *Integrations &
Automation*). An alert resolves itself when the problem is gone (for example after *Renew*); it can be dismissed.
The **dashboard** shows a highlight strip with the products under-licensed or unlicensed, licences expired or ending,
Microsoft 365 problems and devices not activated.

## Software metering (build 121)

Devices with the Zentriq Agent 1.1.1.121 or later (Windows and Linux) record **which programs run, for which user and
for how long**, so licences of software nobody uses can be reclaimed.

* **What is recorded**: once a minute, the programs running in signed-in users' sessions — the program file and the
  user only, never window titles, documents or keystrokes. Per program, user and day the agent keeps the minutes the
  program was running (several windows count once) and how often it was started. Programs of Windows (the Windows
  folder), of Zentriq and, on Linux, of system accounts and the shell are not recorded. The totals of the last 8
  days are sent with every inventory; the device keeps 35 days, the server 400 days.
* **Which program is which product**: each software product has the program files that count as using it
  (*Used when running*, e.g. `winword.exe`, wildcards allowed). Products from the licence catalogue come with them
  (Office, Visio, Project, Acrobat, AutoCAD, Revit, SOLIDWORKS, MATLAB, JetBrains IDEs and about 30 more); add them to
  your own products under *Edit Product*.
* **Used / unused**: an installation is *Used* when one of its programs ran in the last 90 days (*Settings*, 14–365),
  *Unused* when not, although the device has been metering for longer than that, and *Too early to tell* otherwise
  (metering started recently, or the agent is older).
* **Software Usage** tab: the licensed products with installations used and unused and the possible saving (unused ×
  average licence cost), and every program used in the period (devices, users, time running, launches, last use; click
  for the devices and users).
* **Product page**: *Unused Installations* tile, *Last Used* and *Use* per installation, a filter, and **Uninstall from
  N Unused** (permission *software.uninstall*; the agent removes the software as for *Software Inventory*).
* **Device page › Experience**: the programs used on the device in the last 30 days.
* **Alert** (information): "Product: N installation(s) not used for 90 days", with the possible saving.
* **Settings › Software Metering**: on by default; turning it off tells every agent at its next check-in to stop and
  to delete the totals it keeps.

## Microsoft 365 / Entra ID

*Cloud Licences › Connect*: an app registration with the Microsoft Graph **application** permissions
Organization.Read.All, User.Read.All, Application.Read.All, GroupMember.Read.All (optional AuditLog.Read.All for the
last sign-in, which needs Entra ID P1) and admin consent, and a client secret. Zentriq only reads. Every 6 hours (and
*Sync Now*) it reads:

* licences bought and assigned (`subscribedSkus`) and the subscriptions' renewal / expiry dates (`directory/subscriptions`);
* users with their licences, whether the account is enabled and the last sign-in;
* single sign-on applications and the users assigned to them (into Web Apps & Accounts).

Every paid licence type gets a software product and a subscription licence kept current by the sync (quantity from
Microsoft 365, end date = next renewal or expiry). Free and self-service licences are left out.

## API

| Endpoint | |
|---|---|
| `GET /api/v1/sam/summary`, `/sam/highlights` | Tiles, dashboard highlights |
| `GET/POST /api/v1/sam/products`, `GET/PUT/DELETE /sam/products/{id}`, `/sam/products/{id}/installations`, `POST /sam/products/preview`, `GET /sam/discover`, `POST /sam/products/from-catalog` | Products |
| `GET/POST /api/v1/sam/licenses`, `GET/PUT/DELETE /sam/licenses/{id}`, `POST /sam/licenses/{id}/renew`, `POST /sam/licenses/{id}/key`, `POST /sam/licenses/{id}/allocations`, `DELETE /sam/allocations/{id}` | Licences |
| `GET /api/v1/sam/cloud`, `/sam/cloud/users`, `POST/PUT/DELETE /sam/cloud/connections…`, `POST …/{id}/sync` | Microsoft 365 |
| `GET /api/v1/sam/activation` | Windows / Office activation |
| `GET /api/v1/sam/alerts`, `POST /sam/alerts/{id}/dismiss`, `POST /sam/evaluate`, `GET/PUT /sam/settings` | Alerts, settings |
| `GET /api/v1/sam/usage?days=`, `/sam/usage/program?exe=`, `/sam/usage/device/{assetId}` | Software metering |

## Tests

Unit tests (licence position per metric, wildcards and the licence catalogue, activation channels), PostgreSQL tests
(position, alerts opened, resolved by a renewal and re-opened when a licence ends, activation, in-app notification,
Microsoft 365 sync with licences, renewal dates, users and products, more assigned than bought) and the Windows
acceptance test T86 (activation reported by the agent, shortfall and expiry alerts, renewal). First released in
build 1.1.1.112; software metering in build 1.1.1.121 (unit tests of the agent's recorder, PostgreSQL test of used,
unused and recent installations, acceptance test T95). Results: [test report](test-report.md).
