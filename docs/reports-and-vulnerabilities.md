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
| Vulnerability findings | CVE found on a device |

You can only pick columns from a fixed catalogue (`ReportCatalog.cs`). Each column is a SQL expression set by the server. Filter values are always sent as query parameters, and column keys are checked against the catalogue, so a report definition cannot carry SQL.

### Designing a report

1. **New report** › choose a data source.
2. **Layout**:
   - **Detail list**: pick and order the columns.
   - **Summary (count)**: group by up to three columns. The preview then shows a chart, which you can display as donut, pie, bar or column.
3. **Filters**: all conditions must match. The available operators depend on the column type:

   | Column type | Operators |
   |---|---|
   | Text | is, is not, contains, does not contain, starts with, is one of |
   | Number and date | `>`, `≥`, `<`, `≤` |
   | Date only | in the last N days, older than N days (or never) |
   | Every type | is empty, is not empty |

4. **Run** previews the first 500 rows. **Excel** or **CSV** exports up to 100,000 rows.
   - The XLSX file has a frozen header row and an auto-filter, and keeps real numbers and dates.
   - In CSV files, cells that start with `= + - @` get an apostrophe prefix so spreadsheet programs don't run them as formulas.
5. **Save** stores the report. **Share with all users** needs the *dashboards.share* permission or administrator rights.

There are twelve built-in reports, and they are read-only. To change one, use **Save as…** to make your own copy. Administrators can still schedule the built-in reports.

### Scheduling and delivery

- **Schedule** sends the report daily, weekly or monthly, at an hour you choose in the server's time zone, to up to 50 recipients.
- The report goes out as an attachment through the SMTP server set in System Settings › E-mail.
- **Send now** delivers it straight away.
- Every delivery is recorded in **Run history**, and the file is kept there for 30 days. If SMTP isn't configured, the run is marked failed but you can still download the file.

### Permissions

| Permission | Allows |
|---|---|
| `assets.view` | view and run reports |
| `reports.design` | create, edit and schedule reports |
| `reports.export` | export files, send reports and download run files |

## Vulnerability Management

**Console:** Secure & remediate › Vulnerability Management (`/vulnerabilities`), plus a **Vulnerabilities** tab on each device.

### How findings are produced

1. **Software → product mapping.** Each installed application is matched to its NVD CPE `vendor:product`.
   - About 50 widely used products are mapped automatically. Examples: browsers, Microsoft 365, Adobe Reader, Java, 7-Zip, Zoom, PHP, OpenSSL, VLC.
   - You can map other software under **Software mapping**: search NVD, or type the CPE vendor and product. Internal software can be set to **Ignore**.
2. **Feeds.** Three feeds are used:

   | Feed | Data |
   |---|---|
   | NVD CVE API 2.0 | descriptions, CVSS (v3.1 › 3.0 › 4.0 › 2 fallback) and affected version ranges |
   | CISA KEV | CVEs known to be exploited, with the CISA due date and required action |
   | FIRST EPSS | probability of exploitation in the next 30 days |

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
| P1 | On the CISA KEV list, or CVSS ≥ 9.0 with EPSS ≥ 10 % |
| P2 | CVSS ≥ 9.0, or CVSS ≥ 7.0 with EPSS ≥ 10 % |
| P3 | CVSS ≥ 7.0, or CVSS ≥ 4.0 with EPSS ≥ 10 % |
| P4 | Everything else |

The **risk score** (0–100) is CVSS × 7 + EPSS × 20, plus 10 if the CVE is on KEV, capped at 100.

### SLA

- Each finding is due a set number of days after detection. The defaults are critical 15, high 30, medium 90 and low 180; you can change them under Feeds & settings.
- For KEV CVEs, the CISA due date is used when it comes earlier.
- The overview shows overdue findings, the mean time to remediate, and the percentage fixed within SLA over the last 90 days.

### Exceptions

- **Accept risk** needs a reason and an expiry date, at most one year away. When the date passes, the finding reopens on its own.
- **False positive** needs a reason.
- Both are recorded in the audit log and need the *vuln.except* permission.

### Offline servers

Under **Feeds & settings › Import from file**, upload these files, downloaded on a computer with internet access:

| Feed | File |
|---|---|
| NVD | JSON from the NVD CVE API 2.0 (optionally gzip) |
| CISA KEV | `known_exploited_vulnerabilities.json` |
| FIRST EPSS | `epss_scores-YYYY-MM-DD.csv.gz` |

Imports are processed in the background, up to 300 MB per file, and findings are re-evaluated afterwards.

### Permissions

| Permission | Allows |
|---|---|
| `vuln.view` | view findings |
| `vuln.manage` | feeds, mapping and settings |
| `vuln.except` | risk acceptance and false positives |

### Limits

Matching is only as good as the software inventory and the CPE data, so check the following:

- **Unmapped software has no findings.** Review the *Not mapped* filter.
- **Operating-system CVEs aren't covered.** Findings come from installed applications, not from Windows itself. Use Updates for OS patch status.
- **Version numbers are compared numerically.** Products that report a build number in a different form from NVD's (for example a marketing version) can be missed or over-reported. Use *False positive* when that happens.
