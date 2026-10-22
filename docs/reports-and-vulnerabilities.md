# Reports & Analytics and Vulnerability Management

## Reports & Analytics

**Console:** Insight & admin › Reports & Analytics (`/reports`).

### Data sources

| Data source | One row per |
|---|---|
| Assets | active device (identity, OS, hardware, agent coverage, security posture) |
| Hardware | device, with CPU, memory, disks and firmware |
| Installed software | application per device |
| Agents | enrolled agent |
| Activity | service/uninstall/agent-update action |
| Vulnerability findings | CVE found on a device (Windows or an application); needs `vuln.view` |
| Software titles | application title: publisher, devices, versions in use, newest/oldest version, open vulnerabilities |
| Discovery results | address checked by a discovery scan, with the result and open ports |
| Audit trail | administrative action in the audit log; needs `audit.view` |

You can only pick columns from a fixed catalogue (`ReportCatalog.cs`). Each column is a SQL expression set by the server. Filter values are always sent as query parameters, and column keys are checked against the catalogue, so a report definition cannot carry SQL.

### Designing a report

1. **New report** › choose a data source.
2. **Layout**:
   - **Detail list**: pick and order the columns.
   - **Summary**: group by up to three columns. Every group shows its count, and you can add up to six **calculations**: distinct count of any column, or the total, average, minimum or maximum of a number (minimum and maximum also work on dates). Example: average memory and smallest free disk space per hardware model.
   - **Dates by**: when a date column is grouped, choose day, week, month, quarter or year (for example *new devices per month*).
   - The preview shows a chart: donut, pie, bar or column. The chart type is saved with the report and used on the dashboard and in the PDF.
3. **Filters**: all conditions must match. The available operators depend on the column type:

   | Column type | Operators |
   |---|---|
   | Text | is, is not, contains, does not contain, starts with, is one of |
   | Number and date | `>`, `≥`, `<`, `≤` |
   | Date only | in the last N days, older than N days (or never) |
   | Every type | is empty, is not empty |

4. **Run** previews the first 500 rows. **Excel** or **CSV** exports up to 100,000 rows; **PDF** up to 20,000 rows (landscape A4, header on every page, page numbers, and a bar chart for summary reports).
   - The XLSX file has a frozen header row and an auto-filter, and keeps real numbers and dates.
   - In CSV files, cells that start with `= + - @` get an apostrophe prefix so spreadsheet programs don't run them as formulas.
5. **Save** stores the report. **Share with all users** needs the *dashboards.share* permission or administrator rights.

**Add to dashboard** puts a saved report on your dashboard: summary reports as a chart, detail reports as a short table. It runs with the permissions of whoever looks at the dashboard. You can also add saved reports under Dashboard › Customize.

There are 22 built-in reports, and they are read-only. They include *Vulnerability summary by severity*, *Windows updates missing*, *Overdue vulnerabilities*, *Vulnerability aging*, *Most vulnerable devices*, *Hardware summary by model*, *New devices per month*, *Software catalogue*, *Audit trail (30 days)* and *Discovery results (30 days)*. To change one, use **Save as…** to make your own copy. Administrators can still schedule the built-in reports.

### Scheduling and delivery

- **Schedule** sends the report daily, weekly or monthly, at an hour you choose in the server's time zone, to up to 50 recipients, as Excel, PDF or CSV.
- The report goes out as an attachment through the SMTP server set in System Settings › E-mail.
- **Send now** delivers it straight away.
- Every delivery is recorded in **Run history**, and the file is kept there for 30 days. If SMTP isn't configured, the run is marked failed but you can still download the file.

### Permissions

| Permission | Allows |
|---|---|
| `assets.view` | view and run reports |
| `reports.design` | create, edit and schedule reports |
| `reports.export` | export files, send reports and download run files |

Data sources with their own permission (vulnerability findings: `vuln.view`; audit trail: `audit.view`) are checked on every path: the data-source list, preview, export, saving, sending, downloading a run file, and scheduled delivery. A scheduled report runs only while its owner still has that permission.

### IP addresses in reports (build 79)

Every report shows the **IPv4** address by default: the device's primary address when it is IPv4, otherwise the first usable IPv4 address of its adapters (adapters with a gateway first). Devices with no IPv4 address show their IPv6 address. Device datasets also offer an optional **IPv6 address** column. Device names fall back to the IPv4 address when a device has no host name. The same rule applies to Hardware, Software and Network Inventory, security and vulnerability lists, and event notifications.

## Vulnerability Management

**Console:** Secure & remediate › Vulnerability Management (`/vulnerabilities`), plus a **Vulnerabilities** tab on each device.

### How findings are produced

Findings come from two sources: **Windows itself** (missing security updates) and **installed applications**.

#### Windows (operating system)

- **Data:** Microsoft publishes one *Security Update* document per Patch Tuesday (CVRF API: `api.msrc.microsoft.com/cvrf/v3.0`). For each CVE and Windows version it lists the update (KB) and the **fixed build**, for example `10.0.17763.6893`.
- **Devices:** the inventory reports the Windows version and the **UBR** (the monthly patch level, the last part of the build) from the registry, with the agent and with agentless scans. Older agents don't send the UBR; update them from Agents. The overview counts Windows devices without it.
- **Matching:** a device is vulnerable to a CVE when its build is below the fixed build on the same build line, for its exact product (Windows Server 2019, Windows 11 Version 23H2 for x64-based Systems, Server Core installations…). This is the method established scanners use. Hotpatch-only updates are ignored. When Microsoft re-releases a fix, the highest fixed build counts.
- **What it tells you:** the KB that fixes it. A later cumulative update fixes it too.
- **Window:** the last 12 monthly documents are loaded (1–36 in Settings). A month is downloaded again only when Microsoft revises it. Months already loaded are kept, so a device that is far behind still shows older CVEs.
- **Microsoft "Exploited: Yes"** is treated like the CISA KEV list (priority P1).

#### Applications

1. **Software → product mapping.** Each installed application is matched to its NVD CPE `vendor:product`.
   - About 50 widely used products are mapped automatically. Examples: browsers, Microsoft 365, Adobe Reader, Java, 7-Zip, Zoom, PHP, OpenSSL, VLC.
   - You can map other software under **Software mapping**: search NVD, or type the CPE vendor and product. Internal software can be set to **Ignore**.
2. **Feeds.** Three feeds are used:

   | Feed | Data |
   |---|---|
   | NVD CVE API 2.0 | descriptions, CVSS (v3.1 › 3.0 › 4.0 › 2 fallback) and affected version ranges |
   | CISA KEV | CVEs known to be exploited, with the CISA due date and required action |
   | FIRST EPSS | probability of exploitation in the next 30 days |
| Microsoft (MSRC CVRF) | Windows CVEs with KB, fixed build, Microsoft severity, exploited/disclosed |

   - The feeds update daily, or when you press **Update now**.
   - NVD data is fetched only for mapped products and only for changes since the last run.
   - An NVD API key (optional, stored encrypted) makes the NVD download about ten times faster.
3. **Evaluation.**
   - Matching: each installed version is compared with the CPE version ranges (start/end, inclusive/exclusive).
   - Timing: evaluation runs every 30 minutes and after each feed update or import.
   - Lifecycle:
     - A finding closes automatically as **fixed** when the vulnerable version is no longer installed.
     - It reopens if the vulnerable version comes back.

### Prioritisation (risk-based)

| Priority | Rule |
|---|---|
| P1 | Known exploited (CISA KEV, or Microsoft "Exploited: Yes"), or CVSS ≥ 9.0 with EPSS ≥ 10 % |
| P2 | CVSS ≥ 9.0, or CVSS ≥ 7.0 with EPSS ≥ 10 % |
| P3 | CVSS ≥ 7.0, or CVSS ≥ 4.0 with EPSS ≥ 10 % |
| P4 | Everything else |

The **risk score** (0–100) is CVSS × 7 + EPSS × 20, plus 10 if the CVE is known to be exploited, capped at 100. It is then weighted by the device's **business criticality** (asset criticality rating): Critical ×1.25, High ×1.1, Medium ×1, Low ×0.8. You can turn the weighting off under Feeds & settings.

### SLA

- Each finding is due a set number of days after detection. The defaults are critical 15, high 30, medium 90 and low 180; you can change them under Feeds & settings.
- For KEV CVEs, the CISA due date is used when it comes earlier.
- The overview shows overdue findings, the mean time to remediate, and the percentage fixed within SLA over the last 90 days.

### Remediation workflow

- **Status:** *Open → In progress → Fixed*, plus *Risk accepted* and *False positive*. **Fixed** is detected, never set by hand: a finding closes when the next inventory no longer shows the vulnerable version or build.
- **Start remediation** moves findings to *In progress* and assigns them to you if nobody owns them yet. **Assign** sets the owner. **Ticket** stores your service-desk or change number (searchable, and shown in reports). **Add note** records comments.
- **History:** every finding keeps one, covering status changes, assignment, ticket, notes, *fixed* and *reopened* (if the vulnerable version comes back).
- **Re-evaluation** keeps the status, owner and ticket.
- **Permission:** these actions need `vuln.manage`.

### Remediation plan

The **Remediation plan** tab groups open findings by the action that closes them:

- **Windows:** *Install the latest cumulative update for Windows Server 2022 (KB… or later), build … or higher*, with a link to the Microsoft Update Catalog.
- **Applications:** *Update Google Chrome to 130.0.6723.58 or later*.
- **No fix:** *No fixed version published…*, when the vendor hasn't released one.

Each line shows the devices, findings, CVEs, exploited and overdue counts, and the **risk removed** (the sum of the risk scores it closes). Lines are sorted by priority, then risk removed, so the top lines are the best use of a patch window.

### Overview

- **Figures:** open, critical, known exploited (KEV or Microsoft), overdue, affected devices, mean time to remediate and SLA met.
- **Workflow and sources:** Windows vs. application findings, in progress, unassigned, risk accepted and fixed in the last 30 days.
- **Aging:** open findings by how long they have been open (0–30, 31–60, 61–90, 91–180, over 180 days), split by severity.
- **Lists and trend:** top CVEs, riskiest devices and the 30-day trend.

The dashboard can show *Open vulnerabilities* and *Vulnerability aging* charts (Dashboard › Customize).

### Exceptions

- **Accept risk** needs a reason and an expiry date, at most one year away. When the date passes, the finding reopens on its own.
- **False positive** needs a reason.
- Both are recorded in the audit log and need the *vuln.except* permission.

### Offline servers

Under **Feeds & settings › Import from file**, upload these files, downloaded on a computer with internet access:

| Feed | File |
|---|---|
| Microsoft | one month's CVRF document as JSON: `https://api.msrc.microsoft.com/cvrf/v3.0/cvrf/2026-Oct` downloaded with the header `Accept: application/json` |
| NVD | JSON from the NVD CVE API 2.0 (optionally gzip) |
| CISA KEV | `known_exploited_vulnerabilities.json` |
| FIRST EPSS | `epss_scores-YYYY-MM-DD.csv.gz` |

Imports are processed in the background, up to 300 MB per file, and findings are re-evaluated afterwards.

### Permissions

| Permission | Allows |
|---|---|
| `vuln.view` | view findings |
| `vuln.manage` | feeds, mapping and settings; start remediation, assign, ticket, notes |
| `vuln.except` | risk acceptance and false positives |

### Limits

Matching is only as good as the software inventory and the CPE data, so check the following:

- **Unmapped software has no findings.** Review the *Not mapped* filter.
- **Windows needs the full build.** Devices without a UBR in their inventory (agents older than this release, or a scan that could not read the registry) are not checked for Windows CVEs; the overview says how many.
- **Linux and macOS operating systems aren't checked yet.** Their installed applications are, through the NVD mapping.
- **Version numbers are compared numerically.** Products that report a build number in a different form from NVD's (for example a marketing version) can be missed or over-reported. Use *False positive* when that happens.
