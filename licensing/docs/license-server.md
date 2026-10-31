# Zentriq License Server

The license server is the service **Zentriq (the vendor) hosts** for license generation, validation of license
files, check-ins, offline activation of air-gapped servers, and license administration. It is a separate product:
its own build, version, release (`license-server-v...` tags) and documentation (`licensing/docs`). Nothing of it is in
the customer installer, the customer documentation or the customer releases.

## How a customer gets licensed

1. In the portal: create the customer and the license, then **License file for customer** and send the `.zlic` file.
2. The customer imports it in *Zentriq › Licensing*. Zentriq checks the signature and calls `POST /api/v1/activate`
   with the file. The server validates it (license known and active, the newest file issued, an activation free),
   binds it to the customer's installation and returns the bound license with an activation token. Only then is it
   active in the customer's console.
3. Daily check-ins keep it current; suspension, revocation and deactivation reach the customer at the next check-in.
4. Air-gapped: the customer uploads the same file with the server's activation request on the public page (`/`), or
   sends both to you and you use *Offline activation* on the license page.

Source: `licensing/Zentriq.LicenseServer` (ASP.NET Core 10, PostgreSQL). It shares the license contracts and signing
code with the product (`backend/Zentriq.Domain`, `backend/Zentriq.Application`), so both always agree on the format.

## What it does

| Area | Address | Who |
|---|---|---|
| Validation and activation | `POST /api/v1/activate` | Zentriq servers (license file or license key + activation request → signed license + activation token) |
| Check-in | `POST /api/v1/checkin` (Bearer token) | Zentriq servers, daily: usage report → refreshed license, or *suspended / revoked / deactivated* |
| Deactivation | `POST /api/v1/deactivate` (Bearer token) | Zentriq servers (*Licensing › Deactivate*) |
| Offline activation | `/` (page) and `POST /api/v1/offline/activate` | Customers with air-gapped servers: license file (or key) + activation request file → license file |
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
| Generated where it is kept | The setup wizard (or `keygen`) creates it on the license server itself. It is never created on a build machine, emailed or uploaded. |
| Strong algorithm | ECDSA P-256 with SHA-256 (ES256). |
| Encrypted at rest | PKCS#8, AES-256-CBC, key derived with PBKDF2-HMAC-SHA256 and 600,000 iterations from a passphrase of 16+ characters. The file is readable only by its owner (Windows ACL / Unix mode 0600). |
| Passphrase kept apart | Kept in the data folder's `protected` folder, encrypted with Windows DPAPI for the server and readable only by administrators, SYSTEM and the license server service, so the service unlocks the key by itself after a restart. The key file itself is readable only by the service and SYSTEM. |
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
* Network: before the setup is finished, and in *this computer only* mode, the server listens on
  `http://localhost:5080` only. In public mode it adds HTTPS on 443 (Let's Encrypt or your certificate, HSTS) and
  HTTP on 80 only to validate the certificate and redirect to HTTPS. The setup wizard (`/setup`) answers on the
  server itself only, and needs the owner signed in once the owner exists.
* Services: the license server runs as its own virtual account (`NT SERVICE\ZentriqLicenseServer`), PostgreSQL as
  NETWORK SERVICE on 127.0.0.1:5439 with a random password. The data folder is closed to everyone except
  administrators, SYSTEM and these services.

## Install (Windows Server)

The release asset **`Zentriq-LicenseServer-Setup-<version>.msi`** is everything: the license server, its own
PostgreSQL 17 and the setup wizard. Use it the same way for testing on your PC and for the public server.

1. **Run the Setup** as an administrator (Windows Server 2016 or later, or Windows 10/11 for testing). It installs to
   `C:\Program Files\Zentriq License Server`, creates the database and two Windows services that start with
   Windows (*Zentriq License Server* and *Zentriq License Server database*), and opens the setup wizard.
2. **Setup wizard** (`http://localhost:5080/setup`, on the server itself):
   1. *Owner account* of the license portal: your email and a passphrase (12+ characters).
   2. *Signing key*: **Create a new key** (key ceremony: choose a passphrase of 16+ characters, e.g. five random
      words) or **Use a key I created before** (the `.private.pem` from the earlier local edition, with its passphrase).
   3. *Back up the key and send the public key*: download the encrypted key backup and keep it in two offline
      places (passphrase stored separately); download `<key id>.product-entry.json` and send only that to have
      Zentriq built with your key; confirm the fingerprint by phone. Tick *I have stored the key backup*.
   4. *Who can reach the server*: **This computer only** (testing, `http://localhost:5080`), or **Customers over the
      internet**: the server's name (`license.zentriq.com`; its DNS record must point to the server, ports 80 and
      443 open to it) and either a **free certificate obtained and renewed automatically** (Let's Encrypt; accept
      their subscriber agreement, give an email) or **your certificate** (.pfx with its private key). Saving
      restarts the service by itself to apply it (a few seconds).
   5. *Finish* opens the license portal (`/admin`).
3. In the portal: turn on the authenticator app (*Settings*), add the other portal accounts, then customers and
   licenses.

Nothing else is needed: after a reboot both services start and the key is unlocked automatically. The wizard stays
available at `http://localhost:5080/setup` on the server (owner sign-in) to change who can reach the server or to
back up the database at once. Start-menu shortcuts: *Zentriq License Portal* and *Zentriq License Server setup*.

| | |
|---|---|
| Program | `C:\Program Files\Zentriq License Server` (`server`, `pgsql`) |
| Data (kept on uninstall) | `C:\ProgramData\Zentriq\LicenseServer`: `pg` database, `keys` signing key, `protected` passphrases (DPAPI), `certs`, `logs` (server and install logs, 30 days), `backups`, `config.json` (wizard settings) |
| Database backups | Daily, automatically, into `backups` (newest 14 kept); *Back up the database now* in the wizard. Copy them off the server regularly; the signing key is backed up separately (step 3). |
| Upgrade | Run the newer Setup: services stopped, files replaced, database migrated, services started; settings and key kept. |
| Uninstall | *Settings › Apps*: removes the program, services and firewall rule; the data folder stays, so installing again continues with the same key, licenses and settings. |
| Silent install | `msiexec /i Zentriq-LicenseServer-Setup-<version>.msi /qn`, then the wizard at `http://localhost:5080/setup`. |

Testing with a Zentriq server on the **same computer** (once Zentriq trusts your key): *Licensing › License server*
`http://localhost:5080` (plain http is accepted only for localhost), then *Activate online*. Any Zentriq server can use
the files from *Offline activation*.

Command line (`server\Zentriq.LicenseServer.exe`, as administrator): `create-admin --data
"C:\ProgramData\Zentriq\LicenseServer" --email <e>` adds an owner if every owner account is lost (passphrase from
`ZLS_ADMIN_PASSPHRASE` or typed); `install` / `uninstall` are what the Setup runs. The key id and fingerprint are shown in
the wizard and in the portal (*Settings › Signing key*). Settings for a server run without the Setup (development and
tests):

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
| `LicenseServer:TrustAllProxies` | false | Trust X-Forwarded-For/Proto from any proxy (only behind your own reverse proxy) |

### Backup

The installed server backs up its database daily (see *Install*); copy `backups` off the server regularly, and keep
the signing key backup offline (wizard step 3). Losing the database loses the key fingerprints: customers' installed licenses keep working, but online check-ins and new
activations need the licenses to be re-created.

## Tests

`licensing/Zentriq.LicenseServer.Tests` (run in ci-quick): issued-file unit tests, and end-to-end tests against
PostgreSQL — portal sign-in and CSRF, online activation, the activation limit, check-ins with suspension, resume and
portal deactivation, installation deactivation, revocation, offline activation with a key and from the portal,
perpetual support extension, subscription renewal and new keys, the portal's file verification and tamper detection,
and the key ceremony (encryption, wrong passphrase, no overwrite, weak passphrases). Every issued file is verified with
the product's own `LicenseVerifier`.

`SetupWizardTests` (same project): the installed mode before and after the wizard — portal sent to the wizard until it
is finished, owner first, key created or imported and never replaced, backup confirmation, address settings and
certificate checks, automatic key unlock at the next start.

`tests/license-server/Test-LicenseServerSetup.ps1` (license-server workflow, Windows): silent install, services and
their accounts, loopback-only listeners, data folder permissions, the wizard, subscription / perpetual / license-file
activation, restart of both services with automatic unlock, public mode with a certificate (the service restarts itself,
HTTPS with that certificate, HTTP redirect, wizard not reachable from the network), daily backup, uninstall keeping the
data and reinstall.
