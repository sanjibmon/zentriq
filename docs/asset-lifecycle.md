# Asset Lifecycle (IT assets)

**Asset Lifecycle** (build 80, section *Infrastructure*, Standard and Advanced editions) follows every IT asset from purchase to disposal. It applies to IT assets only; non-IT assets keep their own register.

It follows ISO/IEC 19770-1 (IT asset management) and the ITIL 4 IT Asset Management practice. Stages are as in ServiceNow HAM. Disposal records data sanitisation per NIST SP 800-88.

## Stages and statuses

| Stage | Statuses |
|---|---|
| Planned | Requested, Approved |
| On order | Ordered, In transit |
| In stock | New, Refurbished, Reserved, Awaiting deployment |
| In use | In use (not assigned), Assigned to a person, On loan (with return date) |
| In maintenance | Repair, Vendor RMA, Upgrade |
| Missing | Not seen (review), Lost, Stolen |
| Retired | End of life, Pending disposal |
| Disposed | Recycled, Sold, Donated, Returned to lessor, Destroyed |

**Allowed changes**

| From | To |
|---|---|
| Planned | On order, In stock, Retired |
| On order | Planned, In stock, In use |
| In stock | In use, In maintenance, Missing, Retired |
| In use | In stock, In maintenance, Missing, Retired |
| In maintenance | In use, In stock, Retired |
| Missing | In use, In stock, Retired, Disposed |
| Retired | Disposed, In stock |
| Disposed | Nothing (final) |

**Required details**
- *Retired* needs a reason.
- *Lost* and *Stolen* need a description.
- *Disposed* needs the disposal date and the data sanitisation method (Clear, Purge or Destroy, or *not required* for devices without storage), plus who removed the data and when.

**Effects on inventory and licensing**
- Retired and disposed assets leave the active inventory and stop counting for licensing; back to *In stock* restores them.
- Items Planned, On order or In stock do not count for licensing.

**Audit trail:** every change is in the asset's lifecycle history and in the audit log (`lifecycle.*`).

## Automatic changes
- **Existing devices:** every device found by discovery, an agent or Active Directory starts as *In use*. Existing devices became *In use*, or *Retired* if retired, at the upgrade.
- **New purchases:** *Add new assets* registers devices Zentriq has not seen yet, by serial number, as Planned, On order or In stock. When an agent or scan first finds a device with that serial number, the inventory is recorded on that item and it moves to *In use*.
- **Not seen:** a device in use that Zentriq has not seen for **60 days** (setting) becomes *Missing · Not seen* (alert `lifecycle.missing`). It returns to *In use* when seen again.
- **Retired device seen again:** a retired or disposed device that appears on the network again is recorded on the retired asset and raises `lifecycle.retired_seen` (high).
- **Reminders** (Integrations: e-mail, Teams, Slack, webhook, ServiceNow, Jira):
  - warranty ending in 30 days and ended (`lifecycle.warranty`)
  - contract reminder date and end (`lifecycle.contract`)
  - loan past its return date (`lifecycle.loan_overdue`)

## What is recorded per asset
- **Asset tag:** your organisation's own tag — typed on the asset, when adding stock, or in the bulk upload — or given automatically in your format (see *Asset tags* below). Unique across all assets.
- **Custom fields:** your own columns (see *Custom fields* below).
- **Custody:** assigned person and e-mail, since when, loan return date, department and location.
- **Purchase:** acquisition (purchase, lease, rental, donation, BYOD), date, cost, currency (default INR), vendor, invoice, purchase order.
- **Depreciation:** per device class (Policies) or per asset.
  - **Methods:** straight-line, double-declining balance, or none; useful life and salvage value.
  - **Values:** book value today and a yearly depreciation schedule.
  - **Defaults:**

    | Class | Useful life | Refresh after |
    |---|---|---|
    | Laptops | 48 months | 4 years |
    | Desktops | 60 months | 5 years |
    | Mobiles | 36 months | 3 years |
    | Servers | 72 months | 6 years |
    | Network | 84 months | 7 years |
    | Printers | 60 months | 6 years |
    | Virtual machines | not depreciated | — |

    Salvage value is 5 % (0 % for mobiles).
- **Lease:** lessor, start and end, monthly cost.
- **Warranty:** provider, type, start and end.
- **Contracts:** AMC, support, lease, insurance or warranty extension covering many assets, with a reminder N days before the end.
- **Disposal:** date, method, vendor, value recovered, sanitisation method, who and when, certificate reference.

## Asset tags (build 84)

*Policies › Asset tags*:

| Setting | |
|---|---|
| **Automatic, in our format** | New assets get the next tag when they are added (Add new assets), uploaded, or — if *Also tag devices found by agents and scans* is on — found by an agent or scan (hourly). Typed and uploaded tags are kept. *Give asset tags* on a selection uses the same format. |
| **Manual — we use our own tags** | Zentriq never gives a tag by itself. Tags are typed on the asset (Details), entered after each serial number in *Add new assets*, or given in the bulk upload. |
| **Tag format** | Tokens `{PREFIX}` (class prefix: LAP, DSK, MOB, SRV, VM, NET, PRN, AST — editable per class), `{ORG}` (organisation code, up to 10 letters / digits), `{LOC}` (location code, or the first letters of its name), `{YYYY}` `{YY}` `{MM}`, and exactly one running number `{SEQ}` / `{SEQ:n}` (n digits). Anything else: letters, digits and `. _ / -`. Examples: `{PREFIX}-{SEQ:6}` → LAP-000123 (default), `{ORG}-{PREFIX}-{SEQ:5}` → ACME-LAP-00123, `{ORG}/{YYYY}/{LOC}/{SEQ:4}` → ACME/2026/PUNE/0123. |
| **Start numbers at** | The first number for each new prefix / year / location (numbering runs separately for each). Existing numbering continues. |

Tags are up to 40 characters. Existing tags never change when the format changes.

## Custom fields (build 84)

*Policies › Custom fields*: your own fields on IT assets — text, number, date, list of values or yes / no, optionally required.
They appear on the asset's Lifecycle panel and in *Details*, are columns of the bulk upload template (with drop-down lists),
and are checked by type on save and upload. Deleting a field removes its values.

## People (build 84)

The **People** tab is a directory of the people IT assets are assigned to: name, e-mail, employee ID, job title, phone,
department, location, manager, status (Active / Left) and notes, with the IT assets, loans, accessories and book value
each person holds.

- **Add person**, **edit**, **remove** (only when the person holds nothing; otherwise mark them *Left*).
- **Bulk upload** from the People template; rows are matched by employee ID, then e-mail, then name.
- Assigning an asset suggests people from the directory (and fills their e-mail); a **new name** typed there is accepted
  and added to the directory. Renaming a person renames their assignments.
- Name, e-mail and employee ID are unique in the directory. Everyone who held an asset before build 83 was added.
- Department and location boxes everywhere offer **＋ Add new …**.

## Bulk upload (build 84)

**Asset Lifecycle › Bulk upload** (IT assets), **People › Bulk upload**, **Contracts › Bulk upload**:

1. **Download the Excel template** — the columns, an example row, drop-down lists (device types, stages, statuses,
   departments, locations, people, acquisition, depreciation, custom list fields) and an Instructions sheet.
2. Fill one row per item (.xlsx, or CSV with the same headings) and **Check file**: every problem is listed by spreadsheet
   row; nothing is saved.
3. **Upload**: all rows in one transaction — all or nothing.

IT asset rows are matched by **asset tag**, then **serial number**:

- **Existing asset:** only the filled cells change. A different stage, status or person is a lifecycle change with the
  normal rules (recorded in its history). Host name, make and model only fill what the inventory does not know.
- **New asset:** added to the register like *Add new assets* (default **In stock**; Planned, On order, In use or In
  maintenance can be given). It moves to In use by itself when an agent or scan finds the same serial number.
- The tag column sets your own tag; empty tags follow the tag settings.
- Options: update existing rows (or skip them); create departments and locations that do not exist yet. New people named
  in *Assigned to* join the directory.

Contract rows are matched by name; the *Asset tags* column adds assets to the contract. Up to 20,000 rows and 25 MB per file.

## Console
**Infrastructure › Asset Lifecycle**
- **Overview:** assets per stage, book value, warranty and contracts ending, refresh due, missing assets, and items needing attention (no tag, not assigned, no warranty, overdue loans), plus recent changes.
- **IT assets:** filters (stage, status, class, department, location, warranty), sort, column chooser.
  - Bulk actions: change stage, give tags, add to contract, print labels.
- **People:** everyone holding IT assets, with their returnable IT accessories.
  - **Handover form** and **Return form (leaving)**: printable, or save as PDF; signatures for the employee and IT.
- **Contracts:** create, edit, delete; assets covered.
- **Refresh planning:** candidates (past refresh age, warranty ended, operating system out of support, for example Windows 10) and a five-year replacement budget forecast.
- **History:** every lifecycle change.
- **Policies:** tag prefix, depreciation, useful life, salvage %, refresh age per class; currency; not-seen days; automatic tags.

**Device page › Lifecycle tab** (also `/lifecycle/assets/<id>`):
- stage track
- custody, purchase, book value and warranty
- Assign / Check in / Change stage / Dispose / Details
- depreciation schedule and history

**Labels:** QR code (opens the asset in Zentriq), tag, name and serial. Laid out for A4 label sheets of 3 × 8 (70 × 37 mm).

## Reports
- **Data sources:** *IT asset lifecycle* (one row per asset, about 50 columns) and *IT asset lifecycle history*.
- **Built-in reports:**
  - IT asset lifecycle register
  - IT assets by lifecycle stage
  - Warranty ending in 90 days
  - IT asset value and depreciation
  - IT assets by custodian
  - IT asset disposal register
  - IT asset lifecycle history

## API (`/api/v1/lifecycle`, module *lifecycle*)

| Method | Path | |
|---|---|---|
| GET | `/meta`, `/summary` | stages, policies, settings; dashboard figures |
| GET | `/assets?stage&status&class&departmentId&locationId&warranty&person&refresh&untagged&q&sort&page` | list |
| GET / PUT | `/assets/{id}` | detail (history, contracts, schedule) / details (row version) |
| POST | `/assets/{id}/stage`, `/assets/{id}/return`, `/stage` (bulk) | stage changes |
| POST | `/stock` | add Planned / On order / In stock items by serial number |
| POST | `/tags` · GET `/labels?ids=` | asset tags; labels with QR SVG |
| GET / POST / PUT / DELETE | `/people`, `/people/{id}` · GET `/people/suggest?q=` | people directory (list merges what each person holds) |
| GET / POST | `/import/{assets\|people\|contracts}/template` · `/import/{kind}?dryRun=&update=&createMissing=` | Excel template; upload (body = .xlsx / .csv) |
| POST / PUT / DELETE | `/fields`, `/fields/{id}` | custom fields |
| PUT | `/settings` | currency, not-seen days, `tagMode`, `tagFormat`, `tagOrg`, `tagStart`, `tagDiscovered` |
| GET | `/people`, `/people/holdings?person=` | custody |
| GET / POST / PUT / DELETE | `/contracts`, `/contracts/{id}`, POST `/contracts/{id}/assets` | contracts |
| GET | `/refresh`, `/events` | refresh candidates and forecast; history |
| PUT | `/policies/{class}`, `/settings` | *settings.manage* |

**Permissions:** reading needs *assets.view*; changes need *assets.manage*.

## Tests
- **Unit tests:** book value (straight-line, declining balance, none), schedule, allowed transitions.
- **PostgreSQL tests:**
  - assign / loan rules, return, retire (reason), disposal requires sanitisation, final stage, event history
  - a stock item is deployed when first seen; not-seen devices become Missing and come back
  - tags by class prefix
- **Windows acceptance test T72:**
  - stock item with tag and depreciation; duplicate serial refused
  - loan without date refused; assign, people and handover, return
  - stale edit refused (409); contract; refresh candidate; QR label
  - retire, then disposal refused without sanitisation and accepted with it
  - history and the disposal register report
