# Security Pack

From build 1.1.1.127. The Security Pack is a **separately licensed add-on** for Windows devices with the Zentriq
Agent: **endpoint privilege management**, **BitLocker management** and **USB device control**. Console: the menu
module *Security Pack* (pages *Endpoint Privilege*, *BitLocker*, *USB Device Control*). Security Configuration is not
changed: it still only reports.

## Licensing

* Module `securitypack`. It is **not part of an edition** (Standard or Advanced): a license includes it only when it
  lists the module (license server: *extra modules*). The trial includes it.
* It belongs to the *Secure & Remediate* section, so the server needs the Advanced installation (or Secure added with
  Setup › Update).
* Without it the menu shows the pages locked with **Add-on**, the pages explain how to get it, the APIs answer 403, and
  agents receive a policy with everything off (nothing is removed, blocked or encrypted).
* Permissions: `privilege.view`, `privilege.manage`, `bitlocker.view`, `bitlocker.manage`, `bitlocker.recover`
  (show recovery passwords), `devicecontrol.view`, `devicecontrol.manage`. The Administrator role has them.

## Policies

Each area has a policy for **all devices** and optionally one per **device group** (the groups of Software
Deployment, by department, location or device). A device in several groups gets the group policy with the strictest
mode; a device in no group with a policy gets the one for all devices. Changes reach online agents within seconds
(live connection); offline devices get them when they connect and keep applying the last policy while offline.

## Endpoint privilege management

| Mode | Local Administrators group |
|---|---|
| Off | Not touched |
| Audit | Members are reported; those that would be removed are marked |
| Enforce | Every member that is not allowed is removed (checked at once and every 5 minutes) |

Always kept: the built-in Administrator account (RID 500), Domain Admins (512), Enterprise Admins (519), SYSTEM, and
users with **administrator rights for a while**. *Stay administrators* lists other accounts and groups (DOMAIN\name,
local names, SIDs, wildcards). Safety: on a computer without a domain whose built-in Administrator is disabled, nothing
is removed while the policy allows no account (the device reports why).

**Run approved applications as administrator.** Explorer's menu for programs (.exe) and Windows Installer packages
(.msi) and the tray icon get *Run as administrator with Zentriq*. The agent checks the file against the approved
applications — by **publisher** (valid Authenticode signature; optionally a file name), **file hash** (SHA-256) or
**path** — and starts it on the user's desktop **as the user, with administrator rights** (a service-for-user logon of
the same account with the Administrators group added, elevated); no password is asked. The file is kept open from the
check until the program has started, so it cannot be replaced in between. Programs started this way cannot use the
user's network credentials (file shares ask for a password). Not approved: the user can ask IT with a reason.

**Requests.** From the tray or the "not approved" message: an application (with its path, hash and signer) or
administrator rights for a while (15 minutes to 1 day, at most what the policy allows), with a reason. *Requests* in
the console: approve an application **once on this device**, **always on this device**, for a **device group** or
**every device** (a hash rule), or approve administrator rights; or decline with a note. The user gets a message in the
tray.

**Administrator rights for a while.** Approved requests or *Give Rights* in the console: the agent adds the user to the
Administrators group and removes them when the time ends (also offline) or when IT ends them early. A user who was
already an administrator is not changed.

**Activity:** applications run as administrator, not approved, failed; rights given and ended; administrators removed.
Also in the Audit Log (`privilege.*`).

## BitLocker management

| Mode | |
|---|---|
| Off | Nothing collected |
| Report | Status of the system and fixed data drives and their recovery passwords (kept here) |
| Enforce | Report, plus: the system drive is encrypted with **TPM + a recovery password** (no restart needed), optionally fixed data drives (recovery password, unlocked automatically with the system drive); a protected drive without a recovery password gets one |

* Method XTS-AES 256 (default), XTS-AES 128, AES-CBC 256 or 128; used space only (default) or the whole drive.
* Devices without a usable TPM are not encrypted and show the reason (*No TPM*, *TPM not ready*).
* **Recovery passwords** are sent over the agent's mutually authenticated connection and stored encrypted (AES-256-GCM
  with the installation's credential key). The console never lists them: *Recovery Keys* finds a key by the **key ID**
  shown on the BitLocker recovery screen (first characters are enough) or by device; *Show Password* needs
  `bitlocker.recover` and a reason, and is recorded in the Audit Log (`bitlocker.key_view`).
* **New password after it was shown** (default on): the device adds a new recovery password, removes the shown one and
  sends the new one at its next contact. *Replace* does the same on demand. Passwords no longer on a drive are kept,
  marked as such.

## USB device control

| Mode | USB storage (drives, card readers, phones in mass-storage mode) |
|---|---|
| Off | Not checked |
| Audit | Devices and files recorded |
| Read-only | Drives are set read-only: files cannot be copied to them |
| Block | Drives are turned off (the device is disabled) |

* **Allowed devices** are exceptions with full access or read-only: `VID_0781&PID_5581` (every drive of a model),
  `VID_0781&PID_5581\serial` (one drive) or a device instance ID; wildcards; for all computers or a device group. *USB
  Devices › Allow…* fills it in from a device that was seen.
* The agent checks every 3 seconds. Blocked devices are disabled (`pnputil /disable-device`) and enabled again when
  the policy allows them; read-only uses the disk's read-only attribute. Keyboards, mice and other USB devices are
  not affected.
* **Files**: with *Record files*, each file written, renamed or deleted on a USB drive with access is recorded (path,
  size, user, time) — never the content.
* The user sees the policy's message in the tray when a drive is blocked or made read-only.
* Blocking turns the USB device off; where Windows would need a restart for that, the disk is also taken offline so its
  drives disappear at once (both are undone when the policy no longer blocks it).
* The disk Windows runs from (boot, system or the Windows drive, e.g. Windows To Go) is never made read-only or blocked.

## Agent and API

Windows agent 1.1.1.127 or later (capability `security-pack`); Linux agents are not affected. Agent API:
`GET /api/agent/v1/security-pack/policy`, `POST /security-pack/report`, `POST/GET /security-pack/requests`.
Console API under `/api/v1/security-pack/{privilege|bitlocker|usb}`: overview, `PUT /policy`, `DELETE
/policy/{groupId}`, `groups`; privilege: `admins`, `rules`, `requests`, `grants`, `events`; BitLocker: `volumes`,
`keys`, `keys/{id}/reveal`, `keys/{id}/rotate`; USB: `allowed`, `devices`, `events`.

Data kept: events 365 days; recovery passwords until the device is deleted.

## Tests

Unit tests (add-on licensing, elevation rules, administrators that stay, USB access and IDs), PostgreSQL test (device
policy from group and global policies, rules used up, rights for a while, recovery passwords encrypted, revealed,
replaced and retired) and the Windows acceptance tests T96–T98. Results: [test report](test-report.md).
