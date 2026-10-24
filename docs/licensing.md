# Licensing, trial and offline activation

## 14-day trial

* The trial starts **once**, at the moment setup successfully initializes the installation (first administrator
  created). Creating the administrator, the trial record and the "initialized" flag is one database transaction:
  a failed or cancelled installation never starts the trial, and re-running setup, repair, upgrade, restarting
  services or Windows, uninstall + reinstall over the same data never resets it (`trial` has a single row and the
  initialize call is idempotent).
* All modules and features are enabled during the trial. The trial is limited to **30 devices in total** (any mix of
  endpoints, servers, network devices and other assets; key `devices` in the trial policy). Installations whose trial
  started under the earlier per-workload trial policy are moved to the 30-device policy by the 1.1 upgrade.
* The trial row is protected by an HMAC keyed with the per-installation secret (`Secrets\app.secrets`); editing
  the dates in the database invalidates it and disables feature APIs.
* Expiry is enforced by the API: module endpoints (inventory, discovery, dashboards, ...) refuse requests when the trial or license does not cover them, not only hidden in the UI. Expiry never deletes
  data and never blocks sign-in, viewing existing data, backup, restore, export of an activation request, or license import.

## Editions: Standard and Advanced (build 79)

Commercial licences are sold in two editions. The licence names its edition, `"edition": "standard"` or `"advanced"`, and the edition decides the modules:

| Edition | Sections | Modules |
|---|---|---|
| **Standard** | Overview, Discover & inventory, Infrastructure, Insight & admin | dashboards, inventory, discovery, lifecycle, cloud, reports, integrations |
| **Advanced** | Standard + Remote support, Secure & remediate | all modules |

- **Trial:** every section, limited to 30 devices.
- **Extra modules:** a licence can list modules besides its edition, for example a Standard licence with `remote`. Those modules are added.
- **Older licences:** licences without an edition keep their module list.
- **Installed sections:** what the server installed (Standard or Advanced installation) further limits the available sections; see [installation.md](installation.md#installation-types-and-editions).
- **Locked sections:** sections an installed licence does not cover show a lock and an *Advanced* badge in the navigation. Their APIs answer 403 with a clear reason.
- **Licensing page:** shows the edition and, per section, whether it is installed and available.
- **Issuing licences:** the signing tool takes `--edition standard|advanced`. The licence server is a later project; until then, licences are issued offline as described below.

## What is billable

An asset counts against a workload when it is `active`, not merged into another asset, and was seen by discovery
or an agent within the last **30 days** (`licensing.billable_seen_within_days`). Retired, archived and merged
(duplicate) assets are not counted.

| Workload | Asset categories |
|---|---|
| `endpoint` | endpoint (desktops, laptops) |
| `server` | server, virtual_host |
| `network_device` | network_device |
| `other` | printer, iot, other |

A license (or the trial) can also carry a total cap across all workloads (`devices`); it is exact (no overage grace).

Limits are enforced transactionally when an asset becomes billable (discovery, import, manual creation): a
transaction-scoped advisory lock serialises concurrent enrolment so parallel scans cannot both take the last slot.
A 5 % overage grace (`licensing.overage_grace_percent`) applies before new assets are refused. Daily usage
snapshots (`license.usage_snapshot` job) are kept for reconciliation and shown under *Licensing › Usage history*.

## Offline activation (no internet required)

1. Export an activation request (`POST /api/v1/licensing/activation-request`; the *Licensing* page in the console) which returns `activation-request.json` (installation ID,
   product version, current usage). It contains no secrets.
2. Send it to Zentriq. The vendor signs a license with the **vendor signing tool** (`tools/Zentriq.LicenseSigner`,
   never shipped to customers):
   ```
   zentriq-license-signer sign --key zentriq-2027.private.pem --key-id zentriq-2027 --request activation-request.json \
       --customer "Contoso Ltd" --expires 2027-12-31 --grace 14 --entitle endpoint=1000 --entitle server=100 \
       --company "Contoso Private Limited" --contact-name "A. Admin" --contact-email it@contoso.example \
       --contact-phone "+91 80 5555 0101" --address "12 MG Road, Bengaluru 560001" --country India --tax-id 29ABCDE1234F1Z5 \
       --out contoso.zlic
   ```
   The `--company`/`--contact-*` options produce a schema-2 license that carries the **licensee** details.
3. Import the `.zlic` file (`POST /api/v1/licensing/import`; the *Licensing* page in the console). Rejections return HTTP 422 with the reason and are written to the audit log.
   The licensee (company, contact person, e-mail, phone, address, tax ID) is shown under *Licensing › Licensed to* and
   fills the empty fields of the **company profile** (*System Settings › Company profile*), which administrators can
   edit at any time. Signed licensee details themselves cannot be changed without a new license.

### License file format (`.zlic`, schema versions 1 and 2)

```json
{ "format": "zentriq-license", "keyId": "zentriq-2027", "algorithm": "ES256",
  "payload": "<base64 UTF-8 JSON>", "signature": "<base64 ECDSA P-256/SHA-256, IEEE P1363>" }
```
The payload holds `schemaVersion, licenseId, customer, installationId, issuedAt, notBefore, expiresAt, graceDays,
modules, entitlements, requestId`, optionally `edition` (standard | advanced) and, in schema 2, `licensee { company, contactName, contactEmail, contactPhone,
address, country, taxId }`. Zentriq verifies the signature over the exact payload bytes **before** reading any
field, then checks: known and non-revoked key ID, supported schema version, installation binding, validity window
and grace, known modules and workloads, value ranges. Malformed, tampered, expired, wrongly bound, unknown-key and
future-schema licenses are rejected with a specific reason and the attempt is audited. Unsigned entitlement values
are never trusted.

## Keys and rotation

* Only **public** verification keys are compiled into Zentriq
  (`backend/Zentriq.Infrastructure/Resources/license-public-keys.json`); customers cannot add keys.
  The private key never leaves the vendor (offline machine or HSM). `zentriq-dev-2026-10` is a **development key**:
  replace it with your production key before the first customer release.
* Create a key: `zentriq-license-signer keygen --key-id zentriq-2027 --out <secure folder>`.
* Rotation: add the new public key to the JSON (keep the old one), release; sign new licenses with the new key;
  later set `notAfter` on the old key (licenses issued after that date with the old key are rejected) or
  `revoked: true` if it was compromised (all licenses signed by it stop validating – reissue them first).
* Replacing a license: import the new file; it becomes the active license and the previous one is marked *superseded* (kept for audit). Importing the same license ID twice is refused.

## Limits of offline licensing (by design)

* **Revocation**: an offline server cannot learn that a license was revoked. Revocation takes effect only through
  key revocation in a product update, or expiry. Keep license terms short (e.g. annual) for this reason.
* **Clock tampering**: Zentriq stores a monotonic high-water mark of observed UTC time. If the server clock moves
  back more than 24 hours (`clock.rollback_tolerance_minutes`), feature APIs are blocked (data, backup, restore and
  license import still work) until the clock is corrected; expiry is always computed against the later of the clock
  and the high-water mark, so moving the clock back cannot extend a trial or license. Someone with administrative
  control of the server and database can still defeat any purely offline check; the controls make this deliberate,
  detectable (audit log) and contractually clear rather than impossible.
