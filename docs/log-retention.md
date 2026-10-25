# Log retention and export

Zentriq keeps two logs:

* **Audit log** (*Audit Logs*): every administrative and security-relevant action, hash-chained so that changes can be
  detected.
* **Activity log** (*Activity Log*): service actions, software uninstalls, agent and remote-support updates sent to
  devices.

Both are kept for **one year by default**. Organisations that must keep logs longer export them, by hand or on a
schedule, to files they keep for as long as they need.

## Settings (*Audit Logs › Retention & export*)

| Setting | Choices | Default |
|---|---|---|
| Audit log kept for | 3, 6 months; 1, 2, 3, 5, 7, 10 years; for ever | 1 year |
| Activity log kept for | same | 1 year |
| Export entries before they are removed | on / off | on |
| Scheduled export | off, every 3 months (each quarter), every 6 months (Jan–Jun, Jul–Dec), every 12 months (calendar year); audit and / or activity | off |
| Also copy archives to | a Backup & Restore destination (folder, network share, FTP/FTPS, SFTP, Azure Blob, S3, Google Cloud Storage) | this server only |

Changing the settings needs *System settings* (`settings.manage`); viewing and exporting needs *Audit log*
(`audit.view`, plus `activity.view` for the activity log).

**Removal.** The maintenance task (every few minutes) removes entries older than the retention, a whole calendar month
at a time: with one year, entries before the 1st of the month a year ago. When *Export entries before they are removed*
is on, an archive of those entries is made first; if it fails, nothing is removed and administrators get a
notification. Audit entries of the last 90 days can never be removed. The audit log stays append-only for the console:
only the maintenance task, through a database function that runs as the database owner, can remove old entries, and it
records the removal (count and the hash of the last removed entry) as a new `audit.purged` entry, so the chain can be
checked across archives.

**Scheduled exports** cover the period that has just ended: on 1 April the quarter January–March, on 1 July the
half-year January–June, on 1 January the previous year. A failed scheduled export is retried after six hours.

## Export now

*Retention & export › Export now*: choose the dates (the end date is included) and the logs. The archive is made by the
maintenance task within a few minutes and appears in *Exported logs* with a download link.

For a quick spreadsheet, *Download CSV* on *Audit Logs* and *Activity Log* downloads a date range directly.

## Archive files

`C:\ProgramData\Zentriq\Exports\LogArchives\zentriq-logs-<server>-<from>-<to>-<id>.zip` (also copied to the chosen
destination), containing:

* `audit.csv` — id, time (UTC), user, source IP, action, target, outcome, detail (JSON), hash, previous hash
* `activity.csv` — id, requested / completed (UTC), device, IP, kind, operation, target, argument, status, result,
  detail, requested by, channel
* `manifest.json` — period, server, row counts, SHA-256 of each CSV, and the first / last audit entry with its hash

CSV files are UTF-8 with a byte-order mark (open directly in Excel); cells that a spreadsheet would run as a formula
start with an apostrophe. The SHA-256 of the whole zip is shown in the history (hover *Download*). Archives are part of
the report files in a backup; deleting one in the console removes it from the server only, not from the destination.

Every export, download and settings change is recorded in the audit log.
