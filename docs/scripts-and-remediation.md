# Scripts & Remediation

From build 1.1.1.114 scripts can be run on devices with the Zentriq Agent, one-off or on a schedule, with the
output and exit code of every device; a **remediation** runs a detection script first and the fix only where it
finds a problem. Console: *Secure & remediate › Scripts & Remediation* (module *patch*).

## Script library

* Languages: **PowerShell** and **Command Prompt** (Windows), **Bash** and **sh** (Linux), **Python** (either; Python
  must be installed on the device).
* Each script has a name, category, description, default parameters (one line) and a time limit (10 seconds to 4
  hours). Scripts are at most 128 KB; line endings are stored as the device expects them (CRLF / LF).
* **Ready-made scripts**: Clear Temporary Files, Flush DNS Cache, Refresh Group Policy, Restart Print Spooler, Disk
  Space Report, Local Administrators, Detect: Print Spooler Not Running, Fix: Start Print Spooler (Windows); Disk
  Usage Report, Clean Package Caches, Restart a Service, Detect: Root File System Over 90% (Linux). They cannot be
  changed or deleted; *Duplicate* makes an editable copy.

## Running a script

*Run* on a script: parameters, devices (devices, device groups, departments or all devices of the script's
platform), an optional note. Online devices start within seconds, the others when they next connect (up to 24
hours).

On the device the agent:

1. checks the script against the SHA-256 the server sent (over the agent's mutually authenticated connection) and
   refuses it on a mismatch;
2. writes it to a folder only SYSTEM / Administrators (Windows) or root (Linux) can read;
3. runs it as **SYSTEM** / **root**, without a window, with the parameters (Linux: split like a shell would, without
   expansion) — PowerShell with `-NoProfile -NonInteractive -ExecutionPolicy Bypass`;
4. ends the whole process tree when the time limit is reached;
5. sends at most 64 KB of standard output and of standard error, the exit code and the duration; scripts run beside
   the check-in, one at a time.

Exit code 0 is *Done*; any other code (or the time limit) is *Failed*. *Runs* shows every run and, per device, the
status, exit code, duration and output. Waiting devices can be cancelled.

## Remediation and schedules

* **Remediation**: choose a detection script. It runs first: exit code **0** means the device is fine
  (*Compliant*); any other code runs the fix, and then the detection again — *Remediated* when it now passes,
  *Failed* otherwise. The output shows each step.
* **Schedules** run a script or a remediation once, every hour, every day or on chosen weekdays at a time (server
  time) on their devices. A schedule whose script is deleted is refused (change the schedule first).

## Settings and security

* *Allow scripts to run on devices* (permission *settings.manage*): turned off, no script can be started and
  scheduled runs are skipped. Output is kept 90 days (7–3650).
* Permissions: `scripts.view` (library, runs and output), `scripts.manage` (library and schedules), `scripts.run`
  (run on devices). Only the Administrator role has them by default.
* Audit: `script.create`, `script.update`, `script.delete`, `script.run`, `script.remediate` (with the script's
  SHA-256), `script.run_cancel`, `script.schedule_create`, `script.schedule_update`, `script.schedule_delete`,
  `script.settings`. Runs appear in the *Activity Log* (without the script content).
* Installers that a script starts are checked by application control like any other installer.

## API

`GET /api/v1/scripts/summary`, `GET|POST /scripts`, `PUT|DELETE /scripts/{id}`, `POST /scripts/runs`,
`GET /scripts/runs`, `GET /scripts/runs/{id}`, `POST /scripts/runs/{id}/cancel`, `GET|POST /scripts/schedules`,
`PUT|DELETE /scripts/schedules/{id}`, `PUT /scripts/settings`. Agent port: `POST /api/agent/v1/actions/{id}/output`.

## Requirements

The Zentriq Agent of build 1.1.1.114 or later (older agents are skipped and listed).

## Tests

Unit tests (validation, line endings and checksum, schedule times, ready-made scripts), PostgreSQL tests (runs only
on capable devices of the right platform, output only from the device's own agent, remediation payload, ready-made
scripts read-only, turned off, schedules) and the Windows acceptance test T93 (output and exit code as SYSTEM, a
ready-made script, remediation then compliant, time limit, turned off).
