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

## Security

* **Signing key**: ECDSA P-256, loaded from `LicenseServer:SigningKeyFile` at start-up (the service does not start
  without it). Keep it readable only by the service account and keep an offline backup: anyone holding it can issue
  licenses. Its public key must be in the product's `license-public-keys.json` (*Settings › Signing key* shows the
  entry).
* **License keys** are stored as SHA-256 fingerprints; **activation tokens** as SHA-256 hashes.
* **Portal accounts**: passphrase (12+ characters, ASP.NET Core Identity hashing), optional authenticator app
  (TOTP), lockout for 15 minutes after 5 failures, 8-hour sliding sessions in an HttpOnly, SameSite=Strict cookie,
  a custom request header on every change (CSRF), strict Content-Security-Policy. Owners manage accounts.
* Run it behind HTTPS only (the compose file uses Caddy with automatic certificates).

## Deploy (Docker)

From `licensing/`:

1. Create the signing key once, on a trusted machine:
   `dotnet run --project Zentriq.LicenseServer -- keygen --out ./keys --key-id zentriq-prod-2026-11`
   (or with the container: `docker compose run --rm --no-deps license keygen --out /keys`). Copy
   `keys/<id>.private.pem` to `secrets/signing.pem`, store an offline backup, and add
   `keys/<id>.product-entry.json` to `backend/Zentriq.Infrastructure/Resources/license-public-keys.json`, then build
   and release Zentriq.
2. Copy `env.example` to `.env`; set `ZLS_HOSTNAME` (DNS must point to the host), `ZLS_DB_PASSWORD`, `ZLS_KEY_ID`, and
   for the first start `ZLS_BOOTSTRAP_EMAIL` and `ZLS_ADMIN_PASSPHRASE` (the owner account; remove them afterwards).
3. `docker compose up -d`. The database schema is created and migrated automatically at start-up.
4. Sign in at `https://<host>/admin`, turn on the authenticator app, add the other portal accounts.

Without Docker: publish `Zentriq.LicenseServer`, set `ConnectionStrings__LicenseServer` and the `LicenseServer__*`
settings, and run `Zentriq.LicenseServer` (commands: `serve` (default), `migrate`, `keygen --out <dir>`,
`create-admin --email <e> --name <n>` with the passphrase from `ZLS_ADMIN_PASSPHRASE` or standard input).

### Settings

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings:LicenseServer` | — | PostgreSQL connection |
| `LicenseServer:SigningKeyFile` | — | PEM private key |
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
perpetual support extension, subscription renewal and new keys. Every issued file is verified with the product's own
`LicenseVerifier`.
