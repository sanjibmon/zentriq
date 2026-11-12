# Security design

* **Least privilege services** – API and Worker run as per-service virtual accounts (`NT SERVICE\…`), never as
  LocalSystem, a domain administrator or a PostgreSQL superuser. Privileged work (backup, restore, provisioning)
  runs only in `zentriqctl` as SYSTEM/administrator.
* **Database roles** – `zentriq_owner` owns the schema (used only by setup, migrations, backup); `zentriq_app` has
  DML on application tables only, no DDL, no access to role management; neither is a superuser. When Zentriq installs
  PostgreSQL, the generated superuser password is rotated immediately after installation, stored DPAPI-protected for
  SYSTEM/Administrators only, and PostgreSQL listens on localhost only with SCRAM-SHA-256.
* **Secrets** – all generated (no defaults). `Secrets\app.secrets` (app DB password, install secret, PFX passwords, credential key) is
  DPAPI machine-protected and ACL'd to SYSTEM, Administrators and the two service accounts (read);
  `Secrets\owner.secrets` (owner/superuser passwords, backup key) to SYSTEM and Administrators only. No secret is
  written to configuration files, the frontend, the MSI log (hidden properties, hidden custom-action data) or
  application logs (central redaction). Wizard passwords go to `zentriqctl` over stdin, never on a command line or disk.
* **Data folder ACLs** – inheritance from ProgramData removed; SYSTEM and Administrators full control; services get
  only the rights they need (e.g. API: read config/secrets, modify logs/exports/data-protection keys).
* **HTTPS only** – Kestrel with **TLS 1.2 and 1.3 only** on the console and the agent port (TLS 1.0/1.1 refused even where Windows still allows them), HSTS, security headers; customer certificate by
  thumbprint or generated RSA-3072 self-signed certificate. The installer pins the exact certificate when verifying.
* **Web security** – cookie sessions (HttpOnly, Secure, SameSite=Strict; end after 30 minutes without activity and 12 hours after signing in at the latest), anti-CSRF request header on state-changing
  calls, login rate limiting, server-side RBAC on every endpoint, parameterised SQL/EF Core only, request size limits,
  ProblemDetails errors without stack traces, full audit trail.
* **Licensing** – ECDSA P-256 signatures, verification keys compiled in, private key never shipped; see licensing.md.
* **Supply chain** – pinned .NET SDK (global.json), central package versions with NuGet audit (build fails on
  known-vulnerable packages), pinned WiX 5.0.2, third-party installers verified by Authenticode publisher and pinned
  SHA-256 before bundling, release checksums and manifest, optional Authenticode signing of every Zentriq binary,
  the MSI and the bundle (engine detach/reattach).
* **Network** – two inbound ports: the console (HTTPS, default 443) and the agent gateway (8444, mutual TLS, serves
  only `/api/agent/*` and the agent download), with firewall rules scoped as chosen at setup. Discovery uses
  authorised target scopes, bounded concurrency and timeouts.
* **Scan credentials** – encrypted with AES-256-GCM using a per-installation key (`app.secrets`), with the credential
  ID as authenticated data; write-only through the API (never returned), optionally restricted to networks, every
  change audited, usage time recorded. SSH host keys are pinned on first use.
* **Agents** – per-installation agent CA; each agent holds its own non-exportable key and a 1-year client certificate
  whose subject is set by the server. Enrollment codes are random 256-bit secrets stored only as SHA-256 hashes,
  time- and use-limited and revocable; enrollment is rate-limited. Revoked or superseded certificates are refused on
  every request. Agents pin the server key (or require a publicly trusted certificate) and never listen on a port. Agent updates are checked against the SHA-256 from the server and, once agents are code-signed, must be signed by the same publisher as the running agent.

Before going live (see the go-live plan): configure the code-signing certificate (Azure Key Vault or DigiCert
KeyLocker, docs/code-signing.md) and the signature details; keep the production license signing key (`zentriq-prod-2026-11`)
backed up offline and off the build servers; protect the `main` branch and the signing secrets; have an external penetration test
done; review the dependency audit in the quick build's logs (`npm-audit.log`). Build 133 found 4 high findings in MeshCentral 1.2.5's
dependencies; from build 134 they are forced to patched versions (`node-forge` 1.4.3, `compression` 1.8.2,
`image-size` 2.1.0; `installer/remote/package.json`, checked when the installer is built, and MeshCentral's own module
check pointed at those versions). The remaining
moderate findings (`glob`/`minimatch` under `archiver` and `express-handlebars`) concern file-name matching inside the
server, not network input.
