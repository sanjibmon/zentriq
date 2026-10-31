# Licensing internals (vendor side)

Not shipped to customers. Customer-facing documentation: `docs/licensing.md` in the product.

## License file format (`.zlic`, schema 1-3)

```json
{ "format": "zentriq-license", "keyId": "zentriq-prod-2026-11", "algorithm": "ES256",
  "payload": "<base64 UTF-8 JSON>", "signature": "<base64 ECDSA P-256/SHA-256, IEEE P1363>" }
```

The payload holds `schemaVersion, licenseId, customer, installationId, issuedAt, notBefore, expiresAt, graceDays,
modules, entitlements, requestId`, optionally `edition` and `licensee { ... }` (schema 2), and in schema 3:
`licenseType` (subscription | perpetual), `termYears`, `supportUntil`, `activation` and `leaseUntil`.

| `activation` | `installationId` | Meaning |
|---|---|---|
| `certificate` | empty GUID | The license file sent to the customer. The product verifies the signature, then the license server must validate it (known license, active, `file_hash` is the newest file, activation free); it is never stored as a license. |
| `online` | the installation | Returned by online activation and every check-in; carries `leaseUntil`. |
| `offline` | the installation | Returned by offline activation (public page or portal); no lease. |

Entitlement keys: `devices`, `nonit_assets` (missing = unlimited), `cloud_instances`, `technicians`.

The product verifies the signature over the exact payload bytes **before** reading any field, then: known and
non-revoked key, supported schema, content rules (perpetual needs `supportUntil`; `termYears` 1-10; lease not before
issue; a certificate is never bound and carries no lease), installation binding, validity window and grace. A
certificate imported on a product server gives `ActivationRequired`, which starts online validation.

The license server stores the SHA-256 of the newest license file's payload (`licenses.file_hash`); *License file for
customer* in the portal issues a new file and replaces the hash, so earlier files stop activating.

## Keys and rotation

* Only **public** verification keys are compiled into Zentriq
  (`backend/Zentriq.Infrastructure/Resources/license-public-keys.json`); customers cannot add keys.
  `zentriq-dev-2026-10` is a development key, revoked in the build that adds the production key.
* The license server signs with its private key (key ceremony: [license-server.md](license-server.md)); the portal
  shows the entry for `license-public-keys.json` under *Settings › Signing key*.
* Rotation: add the new public key (keep the old one), release; switch the license server to the new key; later set
  `notAfter` on the old key, or `revoked: true` if it was compromised (online servers get re-signed licenses at their
  next check-in; offline servers need new files).
* `licensing/tools/Zentriq.LicenseSigner` remains for emergencies (`--type subscription|perpetual`, `--years`,
  `--support-until`, `--entitle devices=... --entitle nonit_assets=...`).
