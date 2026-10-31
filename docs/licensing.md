# Licensing, trial, free tier and activation

Zentriq is licensed by what it manages. Zentriq sends you a **license file** (`.zlic`). You import it under
*Licensing*; Zentriq checks it and has it **validated by the Zentriq license server** before activating it. Servers
without internet access are activated offline with the same file.

## What is counted (build 93)

| Metric (key) | What counts | How it is sold |
|---|---|---|
| **IT devices** (`devices`) | Every IT device: endpoints (desktops, laptops), servers, network devices (switches, routers, access points, firewalls …), printers and other devices. | Per device |
| **Non-IT assets** (`nonit_assets`) | Non-IT records (furniture, vehicles, equipment …) that are not disposed. One record counts once, whatever its quantity. | Packs: 250, 500, 1,000, 2,000, 4,000 or unlimited |
| **IT accessories** | — | Always unlimited |
| **Cloud and VM instances** (`cloud_instances`) | Virtual machines read from cloud and virtualization connections (Azure, AWS, Google Cloud, vSphere, Hyper-V, Proxmox …). | Per instance |
| **Remote support technicians** (`technicians`) | Active users who hold the *remote.start* right (directly or through a role or group). | Per technician |

* An IT device counts when it is `active`, not merged into another asset, and was seen by discovery or an agent in the
  last **30 days** (`licensing.billable_seen_within_days`). Retired, archived, merged (duplicate), planned, on-order
  and in-stock assets do not count.
* A virtual machine that is also an inventoried device (for example, it runs the agent) counts **once**, as a cloud
  instance, not as an IT device.
* The Licensing page also shows the IT devices by type (endpoints, servers, network devices, other) for information.

### Where limits are enforced

| Metric | Enforced when |
|---|---|
| IT devices | A device becomes billable (discovery, agent, import, manual creation). A transaction-scoped advisory lock serialises concurrent enrolment. Existing devices keep updating at the cap. |
| Non-IT assets | A record is created, or an import would create records (checked with a dry run first; nothing is written when it does not fit). |
| Cloud and VM instances | A cloud sync would store more virtual machines than allowed: the sync job fails and nothing is changed. |
| Technicians | A remote session is started while more users hold *remote.start* than the license allows. Remove the right from users who do not need it (Users, Roles & Groups). |

Licenses allow a **5 % overage grace** (`licensing.overage_grace_percent`) before refusing. The trial and the free
tier are exact. Daily usage snapshots (`license.usage_snapshot`) are kept for reconciliation.

## Plans

| Plan | IT devices | Non-IT assets | IT accessories | Cloud / VM instances | Remote support | Sections |
|---|---|---|---|---|---|---|
| **Trial** (14 days, once) | 30 | 50 | Unlimited | 10 | 1 technician | All |
| **Free** (no end) | 25 | 25 | Unlimited | 5 | None | Standard edition |
| **Licensed** | As purchased | Pack or unlimited | Unlimited | As purchased | As purchased | Standard or Advanced |

### Trial

* Starts **once**, when setup initializes the installation (first administrator created). Re-running setup, repair,
  upgrade, restarting, uninstall + reinstall over the same data never resets it.
* Every section is enabled. Installations whose trial started under an older trial policy get the current limits.
* The trial row is protected by an HMAC keyed with the per-installation secret; editing it in the database
  invalidates the trial (the server then falls back to the free tier).

### Free tier

The installation moves to the free tier automatically when:

* the trial has ended and no license is installed, or
* a subscription has ended and its grace period has passed, or
* the license was revoked, suspended or deactivated at the license server.

The free tier runs the **Standard edition** within the free limits. If the data is above the free limits, the
state is **Free (over limit)**: licensed features pause until a license is activated or the records are reduced.
Data is **never** deleted, and sign-in, viewing data, backup, restore and licensing always keep working.

### Licenses: subscription or perpetual

* **Subscription** for **1, 3 or 5 years**. Support and updates are included for the term. After the end, a grace
  period (normally 30 days) follows, then the free tier.
* **Perpetual** with **yearly support**. The license never expires. Support can be extended one or more years at a
  time (multi-year on demand). A Zentriq version released **after** the end of support does not run on that license
  (*Support expired*): renew support, or reinstall the earlier version. Each Zentriq build carries its release date
  (`ProductInfo.ReleaseDate`, set at build time) for this check.
* **Editions**: Standard (Overview, Discover & inventory, Infrastructure, Insight & admin) or Advanced (everything,
  including Remote support and Secure & remediate). A license can add single modules to its edition.

## Activation with the license file (normal case)

1. *Licensing › Activate › Import license file*: choose the `.zlic` file from Zentriq.
2. Zentriq checks the file's signature (it must come from Zentriq, unchanged), then sends it with an activation
   request (installation ID, version, organization, current usage; no secrets) to the Zentriq license server over
   HTTPS.
3. The license server **validates** it: the license exists, is active (not suspended or revoked), the file is the
   newest one issued for it, and an activation is free. It returns the license bound to this installation and an
   activation token (stored encrypted). **Only then is the license activated.** If the license server cannot be
   reached or refuses the file, nothing changes and the reason is shown.
4. The server **checks in daily** (hourly in the last 10 days of the lease). Each check-in reports usage and receives
   a refreshed license: renewals, upgrades, entitlement changes and support extensions arrive automatically.
   *Check in now* runs one at once.

* **License key**: a license can also be activated with its key (`ZQL-XXXXX-XXXXX-XXXXX-XXXXX`) under *Activate with
  a license key instead*; the license server validates it the same way.
* **New license file**: if Zentriq issues a new file for your license (e.g. after it was lost or shared), the earlier
  file can no longer activate; servers that are already activated keep running.
* **Lease**: an online license must be refreshed within **30 days**. When the lease ends without a check-in, a
  warning appears and the license keeps working for **30 more days**; then the state is *Lease expired* and features
  pause until the server can check in (or is activated offline).
* **Suspended / revoked / deactivated** at the license server: the next check-in removes the license and the server
  falls back to the free tier. A suspended license returns at the first check-in after it is resumed.
* **Moving to another server**: *Deactivate* releases the activation on the license server; import the license file
  on the new server. A license activates on as many servers as it allows (normally one).
* **License server address**: `https://license.zentriq.com` (*Licensing › License server*, system setting
  `licensing.server_url`; change it only when Zentriq asks). The server's outbound proxy settings apply; port 443
  must be allowed. Plain `http://` is accepted only for `localhost`.

API: `POST /api/v1/licensing/import {document}` (license file: validated online; file for this server: imported),
`POST /licensing/activate {licenseKey}`, `POST /licensing/checkin`, `POST /licensing/deactivate`,
`PUT /licensing/server {url}`, `GET /licensing/status`. All require *licensing.manage* (status: signed in).

## Offline activation (air-gapped servers)

1. *Licensing › Offline activation › Download activation request* (`POST /api/v1/licensing/activation-request`).
2. On any computer with internet access, open `https://license.zentriq.com/`, upload the **license file** from
   Zentriq (or enter the license key) together with the activation request, and download the license file for this
   server. Alternatively send both files to Zentriq.
3. *Import license file* with the file you received for this server.

The license server validates the license the same way as online. Offline licenses carry no lease and do not check
in: import a new file after a renewal, upgrade or support extension. Perpetual licenses are activated offline in the
same way.

## License files

Two kinds of `.zlic` file exist, both signed by Zentriq (ECDSA P-256 / SHA-256) and checked before any field is read:

| File | Bound to | Use |
|---|---|---|
| License file from Zentriq | no server | Proof of purchase: activates only after the license server validates it. |
| License for this server | this installation | Returned by the license server (online, or offline activation). Carries the entitlements, term or support and, online, the lease. |

Zentriq trusts only the Zentriq signing keys built into the product; a changed file is rejected. A newer license with
the same ID (a refreshed online license or a re-issued file) replaces the current one; the previous record is kept
as *superseded*.

## Limits of offline licensing (by design)

* **Revocation** reaches offline servers only through key revocation in a product update, or the end of the term.
* **Clock tampering**: Zentriq keeps a high-water mark of observed UTC time. If the clock moves back more than
  24 hours (`clock.rollback_tolerance_minutes`), features pause until it is corrected; expiry is computed against
  the later of the clock and the high-water mark.
