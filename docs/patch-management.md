# Patch Management

From build 1.1.1.114 the Zentriq Agent finds the operating system updates a device is missing and installs the
ones you choose, on Windows and Linux. Console: *Secure & remediate › Patch Management* (module *patch*, the
*Secure & remediate* section of an Advanced installation).

## How updates are found

| Platform | Source | How |
|---|---|---|
| Windows | The device's own Windows Update configuration: Windows Update / Microsoft Update, or **WSUS** when a Group Policy sets a WSUS server | Windows Update Agent (`Microsoft.Update.Session`), driven by PowerShell 5.1 as SYSTEM: updates that are not installed and not hidden |
| Debian, Ubuntu | The device's apt repositories | `apt-get update`, `apt list --upgradable`; packages from a `-security` suite are *Security Updates* |
| RHEL and its rebuilds, Fedora, Amazon Linux | The device's dnf / yum repositories | `check-update`; `updateinfo` marks security advisories and their severity |
| SUSE | The device's zypper repositories | `list-updates` (classified as *Updates*) |

Zentriq does not change where devices get their updates; it does not download updates itself.

* **Scans**: automatic every 24 hours (*Settings*: 1–168 hours, or off), and on demand (*Scan Now* on a device,
  *Scan All Devices* on the overview). A scan runs beside the agent's check-in, so the device stays reachable.
* Each update is stored with its KB number, classification (*Critical Updates*, *Security Updates*, *Definition
  Updates*, *Updates*, *Update Rollups*, *Feature Packs*, *Service Packs*, *Drivers*, *Tools*, *Upgrades* — by
  category ID, so it also works on non-English Windows), MSRC severity, product, release date, size, whether it may
  need a restart, its CVEs and its support link.
* Per device and update: *Missing*, *Failed* (the last installation failed; the error is shown), *Restart Pending*
  or *Installed*. An update that disappears from a device's scan counts as installed (by Zentriq, by Windows Update
  itself or by hand). On Linux, a newer version of a package replaces the older one.
* A scan that cannot run says why — for example *The Windows Update service is disabled on this device*, *The
  device cannot reach its update server*, or a policy that blocks Windows Update.

## Approve, decline, deploy

* *Missing Updates*: filter by state, platform, classification, severity and approval; search by title, KB, package
  or CVE. Select updates to **Approve**, **Decline** or **Reset**, or **Install** them.
* *Devices*: every device with the Zentriq Agent with its last scan, missing updates (critical / security marked),
  failures and restarts pending. Open a device to see its updates, install selected ones or *Install All Missing*.
* **Deploy** to devices, device groups (from Software Deployment), departments or all devices. Each device installs
  only what it is missing; *Install all missing* takes the chosen classifications and leaves declined updates out.
* Restart: **Restart when the updates need it** (signed-in users get a notice; *Settings*: 1–240 minutes, own
  message) or **Do not restart** (the device is shown with *Restart Pending*).
* After installing, the agent scans again, so the console shows the new state at once. *Deployments* lists every
  deployment with its progress per device; waiting devices can be cancelled. Offline devices install when they next
  connect (up to 7 days).
* Windows: download and installation through the Windows Update Agent as SYSTEM (EULAs accepted). Linux: only
  upgrades of installed packages (`apt-get install --only-upgrade`, `dnf upgrade`, `yum update`, `zypper update`);
  the package manager runs under the agent's own application control exemption.

## Automatic deployment (policies)

A policy installs the missing updates it covers on its devices in a **maintenance window**, once per window:

* platform; classifications; severities (none: any); *only approved updates*; *wait N days after the release*;
* devices, device groups, departments or all devices;
* days of the week, start time (server time) and length (1–24 hours; it may run past midnight);
* restart behaviour.

Declined updates are never installed by a policy. Offline devices install when they next connect.

## Permissions

| Permission | |
|---|---|
| `patch.view` | See Patch Management (new; the *Patch Manager* role has it) |
| `patch.approve` | Approve and decline, policies and settings |
| `patch.deploy` | Scan devices and install updates |

## Requirements and limits

* The Zentriq Agent of build 1.1.1.114 or later (older agents are listed and skipped; update them in *Agents*).
* Windows: the Windows Update service must not be disabled. Third-party application updates are not part of this
  release (use Software Deployment).
* Linux: zypper updates are not classified as security updates yet.

## Audit

`patch.scan`, `patch.deploy`, `patch.deploy_cancel`, `patch.approve`, `patch.decline`, `patch.approval_reset`,
`patch.policy_create`, `patch.policy_update`, `patch.policy_delete`, `patch.settings`. Device actions (scan,
install) appear in the *Activity Log*.

## API

`GET /api/v1/patches/summary`, `GET /patches` (filters `q`, `platform`, `classification`, `severity`, `approval`,
`state`), `GET /patches/{id}/devices`, `POST /patches/approval`, `GET /patches/devices`, `GET /patches/devices/{id}`,
`POST /patches/scan`, `POST /patches/jobs`, `GET /patches/jobs`, `GET /patches/jobs/{id}`, `POST /patches/jobs/{id}/cancel`,
`GET|POST /patches/policies`, `PUT|DELETE /patches/policies/{id}`, `PUT /patches/settings`. Agent port:
`POST /api/agent/v1/patches`.

## Tests

Unit tests (classification and severity mapping, update ids, maintenance windows, apt / dnf / dnf 5 / zypper output),
PostgreSQL tests (scans, failed installations and restarts, deployments send only what is missing, declined and
delayed updates, Linux versions, policies once per window, automatic scans) and the Windows acceptance test T92.
