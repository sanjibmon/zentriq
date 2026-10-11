# Security design

* **Least privilege services** – API and Worker run as per-service virtual accounts (`NT SERVICE\…`), never as
  LocalSystem, a domain administrator or a PostgreSQL superuser. Privileged work (backup, restore, provisioning)
  runs only in `zentriqctl` as SYSTEM/administrator.
* **Database roles** – `zentriq_owner` owns the schema (used only by setup, migrations, backup); `zentriq_app` has
  DML on application tables only, no DDL, no access to role management; neither is a superuser. When Zentriq installs
  PostgreSQL, the generated superuser password is rotated immediately after installation, stored DPAPI-protected for
  SYSTEM/Administrators only, and PostgreSQL listens on localhost only with SCRAM-SHA-256.
* **Secrets** – all generated (no defaults). `Secrets\app.secrets` (app DB password, install secret, PFX password) is
  DPAPI machine-protected and ACL'd to SYSTEM, Administrators and the two service accounts (read);
  `Secrets\owner.secrets` (owner/superuser passwords, backup key) to SYSTEM and Administrators only. No secret is
  written to configuration files, the frontend, the MSI log (hidden properties, hidden custom-action data) or
  application logs (central redaction). Wizard passwords go to `zentriqctl` over stdin, never on a command line or disk.
* **Data folder ACLs** – inheritance from ProgramData removed; SYSTEM and Administrators full control; services get
  only the rights they need (e.g. API: read config/secrets, modify logs/exports/data-protection keys).
* **HTTPS only** – Kestrel with TLS (OS-negotiated TLS 1.2/1.3), HSTS, security headers; customer certificate by
  thumbprint or generated RSA-3072 self-signed certificate. The installer pins the exact certificate when verifying.
* **Web security** – cookie sessions (HttpOnly, Secure, SameSite=Strict), anti-CSRF request header on state-changing
  calls, login rate limiting, server-side RBAC on every endpoint, parameterised SQL/EF Core only, request size limits,
  ProblemDetails errors without stack traces, full audit trail.
* **Licensing** – ECDSA P-256 signatures, verification keys compiled in, private key never shipped; see licensing.md.
* **Supply chain** – pinned .NET SDK (global.json), central package versions with NuGet audit (build fails on
  known-vulnerable packages), pinned WiX 5.0.2, third-party installers verified by Authenticode publisher and pinned
  SHA-256 before bundling, release checksums and manifest, optional Authenticode signing of every Zentriq binary,
  the MSI and the bundle (engine detach/reattach).
* **Network** – one inbound port (HTTPS) with a firewall rule scoped as chosen at setup. Discovery uses authorised
  target scopes, bounded concurrency and timeouts.

Known gaps before production: code-signing certificate not yet configured; production license key not yet generated;
external penetration test not yet performed; remote support, patch deployment and endpoint remediation modules are
not implemented (the API reports them as unavailable).
