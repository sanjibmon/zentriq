# Zentriq License Server

The license server is the service **Zentriq (the vendor) hosts** for online activation, check-ins, offline
activation of air-gapped servers, and license administration. It is not part of the customer installer.

Source: `licensing/Zentriq.LicenseServer` (ASP.NET Core 10, PostgreSQL). It shares the license contracts and signing
code with the product (`backend/Zentriq.Domain`, `backend/Zentriq.Application`), so both always agree on the format.

## What it does

| Area | Address | Who |
|---|---|---|
| Online activation | `POST /api/v1/activate` | Zentriq servers (license key + activation request → signed license + activation token) |
| Check-in | `POST /api/v1/checkin` (Bearer token) | Zentriq servers, daily: usage report → refreshed license, or *suspended / revoked / deactivated* |
| Deactivation | `POST /api/v1/deactivate` (Bearer token) | Zentriq servers (*Licensing › Deactivate*) |
| Offline activation | `/` (page) and `POST /api/v1/offline/activate` | Customers with air-gapped servers: license key + activation request file → license file |
| License portal | `/admin` | Zentriq staff: customers, licenses, renewals, support, activations, audit |
| Health | `GET /health` | Monitoring |

Public endpoints are rate limited per client address (`LicenseServer:RequestsPerMinute`, default 30). Errors are
RFC 7807 problem documents; the product shows their `title` to its administrator.

## Licenses in the portal

* **Customers**: company and contact details. They are signed into the license file and shown in the customer's
  console (*Licensing › Licensed to*).
* **New license**: customer, edition (Standard / Advanced), type and term, and entitlements:
  * subscription for 1, 3 or 5 years, or perpetual with 1–10 years of support;
  * IT devices (per device), non-IT pack (250 / 500 / 1,000 / 2,000 / 4,000 / unlimited), cloud and VM instances,
    remote support technicians; IT accessories are always unlimited;
  * servers that may activate the license (default 1), grace days (default 30), extra modules.
  The **license key is shown once**; only its SHA-256 fingerprint is stored. *New key* replaces a lost or leaked key
  (servers already activated keep working).
* **Renew** (subscriptions): 1, 3 or 5 more years from the current end (from today when it has passed).
* **Extend support** (perpetual): 1–10 more years, as often as needed.
* **Change entitlements**: edition, counts, pack, modules, activation limit. Online servers receive the change at
  their next check-in.
* **Suspend / Resume / Revoke**: online servers fall back to the free tier at their next check-in; resume restores
  the license. Revocation is final and the key stops activating.
* **Activations**: each server's installation ID, mode (online / offline), version, last check-in and last reported
  usage. *Deactivate* frees the slot (the server learns it at its next check-in).
* **Offline activation**: upload the request a customer emailed; download the license file for them.
* **Issued license files**: every file the server signed (download for support); **History** and **Audit log**:
  every change, sign-in and activation.
* **Dashboard**: renewals and support ending within 90 days, servers that have not checked in for 7 days.

## Production signing key (key ceremony)

The signing key is what makes a Zentriq license genuine. It follows the usual practice for code- and license-signing
keys:

| Practice | How |
|---|---|
| Generated where it is kept | `keygen` (or `zls setup`) creates it on the license server's own machine. It is never created on a build machine, emailed or uploaded. |
| Strong algorithm | ECDSA P-256 with SHA-256 (ES256). |
| Encrypted at rest | PKCS#8, AES-256-CBC, key derived with PBKDF2-HMAC-SHA256 and 600,000 iterations from a passphrase of 16+ characters. The file is readable only by its owner (Windows ACL / Unix mode 0600). |
| Passphrase kept apart | Docker: a separate secret file. Local edition: protected for the Windows user with DPAPI (or asked at every start with `-NoStorePassphrase`). |
| Identified and verifiable | Key id (`zentriq-prod-YYYY-MM`) and SHA-256 fingerprint of the public key, written to `<id>.ceremony.txt` and shown in the portal (*Settings › Signing key*). Confirm the fingerprint by a second channel when the public key is handed over. |
| Only the public key travels | `<id>.product-entry.json` goes into `backend/Zentriq.Infrastructure/Resources/license-public-keys.json`; Zentriq trusts only keys compiled into the build. |
| Self-tested | At every start the service signs a probe and verifies it with Zentriq's own verifier; a wrong key or passphrase stops the start with a clear message. |
| Backed up offline, rotatable | Two offline copies of the encrypted key, passphrase stored separately. Rotation: new key, Zentriq release with both public keys, switch the server, then retire the old key (`notAfter`) or revoke it if compromised. |
| Next step for the public server | Move the key into a hardware-backed store (HSM, or a cloud KMS such as Azure Key Vault Managed HSM) so it cannot be copied at all. |

`zentriq-dev-2026-10`, the key the product trusts today, is a development key; it is removed (revoked) in the build
that adds your production key.

## Security

* **Signing key**: see above; loaded from `LicenseServer:SigningKeyFile` at start-up.
* **License keys** are stored as SHA-256 fingerprints; **activation tokens** as SHA-256 hashes.
* **Portal accounts**: passphrase (12+ characters, ASP.NET Core Identity hashing), optional authenticator app
  (TOTP), lockout for 15 minutes after 5 failures, 8-hour sliding sessions in an HttpOnly, SameSite=Strict cookie,
  a custom request header on every change (CSRF), strict Content-Security-Policy. Owners manage accounts.
* Public hosting: HTTPS only (the compose file uses Caddy with automatic certificates). The local edition listens on
  `localhost` only.

## Local edition for Windows (test before going public)

Release asset `Zentriq-LicenseServer-Local-win-x64-<version>.zip`: the license server (self-contained, no .NET
install needed), a portable PostgreSQL 17 (EnterpriseDB binaries) and `zls.cmd`. Nothing is installed; everything it
creates is in the folder's `data` subfolder. PostgreSQL listens on 127.0.0.1:5439, the server on
`http://localhost:5080`.

1. Unpack to a folder you control, e.g. `C:\ZentriqLicense`.
2. `zls setup`: database (its password kept with DPAPI), **key ceremony** (asks for the signing key passphrase),
   owner account of the portal (asks for your email and a portal passphrase). Prints the key id and fingerprint.
3. `zls start` opens `http://localhost:5080/admin` (`zls start -Background` returns to the prompt; `zls stop`).
4. Generate licenses: *Customers › New customer*; *Licenses › New license*; on the license, *Offline activation*
   (attach a real activation request from a Zentriq server, or tick *test file*); *Verify file* shows exactly what
   was signed and checks the signature as Zentriq does.
5. Back up `data\keys\<id>.private.pem` offline; send only `data\keys\<id>.product-entry.json` to have Zentriq
   built with your key.

Other commands: `zls status`, `zls backup` (database dump into `data\backups`), `zls key-info`.

Once Zentriq trusts your key, a Zentriq server on the **same computer** can activate online against the local
server: *Licensing › License server* `http://localhost:5080` (plain http is accepted only for localhost), then
*Activate online*. Any Zentriq server can use the files from *Offline activation*.

## Deploy (Docker, public)

From `licensing/`:

1. Create the signing key once, on the host:
   `docker compose run --rm --no-deps -it license keygen --out /keys` (asks for the passphrase). Copy
   `keys/<id>.private.pem` to `secrets/signing.pem` and the passphrase (one line) to `secrets/signing.passphrase`;
   store an offline backup; send `keys/<id>.product-entry.json` for the Zentriq build. To move the key created with
   the local edition instead, copy its `.private.pem` and passphrase the same way and keep the same key id.
2. Copy `env.example` to `.env`; set `ZLS_HOSTNAME` (`license.zentriq.com`; DNS must point to the host),
   `ZLS_DB_PASSWORD`, `ZLS_KEY_ID`, and for the first start `ZLS_BOOTSTRAP_EMAIL` and `ZLS_ADMIN_PASSPHRASE` (the owner
   account; remove them afterwards).
3. `docker compose up -d`. The database schema is created and migrated automatically at start-up.
4. Sign in at `https://<host>/admin`, turn on the authenticator app, add the other portal accounts.

Without Docker: publish `Zentriq.LicenseServer`, set `ConnectionStrings__LicenseServer`, the `LicenseServer__*`
settings and `ZLS_SIGNING_PASSPHRASE`, and run it. Commands: `serve` (default), `migrate`, `keygen --out <dir>
[--key-id <id>]`, `key-info`, `create-admin --email <e> --name <n>` (portal passphrase from `ZLS_ADMIN_PASSPHRASE` or
standard input).

### Settings

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings:LicenseServer` | — | PostgreSQL connection |
| `LicenseServer:SigningKeyFile` | — | Encrypted PEM private key |
| `LicenseServer:SigningKeyPassphraseFile` | — | File with the key passphrase (else `ZLS_SIGNING_PASSPHRASE`) |
| `LicenseServer:SigningKeyId` | `zentriq-prod-2026` | Key id written into license files (must match the product entry) |
| `LicenseServer:PublicUrl` | `https://license.zentriq.com` | Public address |
| `LicenseServer:LeaseDays` | 30 | Online lease length |
| `LicenseServer:RequestsPerMinute` | 30 | Public API rate limit per address |
| `LicenseServer:TrustAllProxies` | false | Trust X-Forwarded-For/Proto from any proxy (set in the compose file) |

### Backup

Back up the PostgreSQL database (`pg_dump zentriq_license`) daily and the signing key once (offline). Losing the
database loses the key fingerprints: customers' installed licenses keep working, but online check-ins and new
activations need the licenses to be re-created.

## Tests

`licensing/Zentriq.LicenseServer.Tests` (run in ci-quick): issued-file unit tests, and end-to-end tests against
PostgreSQL — portal sign-in and CSRF, online activation, the activation limit, check-ins with suspension, resume and
portal deactivation, installation deactivation, revocation, offline activation with a key and from the portal,
perpetual support extension, subscription renewal and new keys, the portal's file verification and tamper detection,
and the key ceremony (encryption, wrong passphrase, no overwrite, weak passphrases). Every issued file is verified with
the product's own `LicenseVerifier`.

`tests/license-server/Test-LocalLicenseServer.ps1` (windows-release) unpacks the local edition and runs it under
Windows PowerShell 5.1: setup and key ceremony, start, sign-in, subscription and perpetual licenses, offline and online
activation, a support extension arriving by check-in, tamper detection, backup and stop.
