# Device Experience

From build 1.1.1.120 every device with the Zentriq Agent (Windows and Linux) gets an **experience score** from 0 to
100: how well it works for the person who uses it. Console: *Discovery & Inventory › Device Experience*, and the
*Experience* tab of each device. Permission: *assets.view*.

## What the agent reports

| | Windows | Linux |
|---|---|---|
| Start-up time | Event 100 of *Diagnostics-Performance*: time until the desktop appears, and the time after it until the device is ready | `systemd-analyze` (current boot) |
| Application crashes | *Application Error* 1000 (program, version, faulting module) | Programs that dumped core (systemd-coredump) |
| Applications not responding | *Application Hang* 1002 | — |
| Blue screens | *WER-SystemErrorReporting* 1001 (stop code) | — |
| Unexpected shutdowns | *Kernel-Power* 41 | — |
| Service failures | *Service Control Manager* 7031, 7034 | `Failed with result` of systemd units; out-of-memory kills |
| Processor and memory | Sampled every minute: average per day and minutes at 90 % or more | Same (from /proc) |
| Free space on the system drive, battery wear, uptime | Yes | Yes |

The agent reads the event logs of the last 8 days at each inventory; samples are kept on the device for 35 days.
Nothing personal is collected (no window titles, documents or keystrokes).

## The score

100 minus points for each problem over the last 7 days (never below 0): good from 80, fair from 60, poor below.

| Problem | Points |
|---|---|
| Start-up (median) over 30 s / 60 s / 120 s | 5 / 12 / 20 |
| Ready after the desktop appears over 60 s / 120 s | 4 / 8 |
| Application crash | 4 each, at most 25 |
| Application not responding | 2 each, at most 15 |
| Blue screen | 15 each, at most 30 |
| Unexpected shutdown | 5 each, at most 15 |
| Service failure or out-of-memory kill | 2 each, at most 10 |
| Processor at 90 % or more over 10 % / 20 % of the time | 6 / 12 |
| Memory 90 % or more in use over 10 % / 20 % of the time | 6 / 12 |
| System drive under 10 % / 5 % free | 8 / 15 |
| Battery under 80 % / 60 % of its design capacity | 3 / 8 |
| Not restarted for over 14 / 30 days | 2 / 5 |

## Console

* **Device Experience**: the average score, devices good / fair / poor, the median start-up time, crashes and blue
  screens of the week, the score over 30 days, the most common problems (click to list the devices), the applications
  that crash most, and every device with its score and main problem (lowest first; filters by rating, problem,
  device or user).
* **Device › Experience**: the score and each problem with its points, the score over 30 days, processor and memory per
  day, the events of 30 days, and the programs used (software metering).

Data is kept 120 days (samples and events) and 400 days (daily scores).

## API

`GET /api/v1/experience/summary`, `GET /experience/devices?rating=&issue=&q=`, `GET /experience/devices/{assetId}`.

## Tests

Unit tests (the agent's recorder, the score), PostgreSQL test (a report stored with the inventory, sent twice without
duplicates, the score) and the Windows acceptance test T95. Results: [test report](test-report.md).
