# Administrator guide

## Day-to-day commands (`C:\Program Files\Zentriq\tools\zentriqctl.exe`, elevated prompt)

| Command | Purpose |
|---|---|
| `zentriqctl status` | One-line health per component |
| `zentriqctl verify` | Full end-to-end check (services, PostgreSQL as the app role, schema, worker heartbeat, HTTPS readiness, license state, console page) – exit code 0/1 |
| `zentriqctl diagnose` | Writes a redacted `Logs\diagnostics-<time>.zip` for support (no secrets files, passwords masked) |
| `zentriqctl repair` | Re-runs provisioning with the saved settings (re-applies ACLs, roles, migrations, certificate binding, service recovery, scheduled tasks). Same as *Repair* in *Apps & features* |
| `zentriqctl backup` / `backup-verify` / `restore` | See [backup-restore.md](backup-restore.md) |

## Services

| Service | Account | Notes |
|---|---|---|
| Zentriq API (`ZentriqApi`) | `NT SERVICE\ZentriqApi` | HTTPS console + REST API. Delayed automatic start, restart after 5 s / 15 s / 60 s on failure |
| Zentriq Worker (`ZentriqWorker`) | `NT SERVICE\ZentriqWorker` | Durable jobs (discovery, reports, usage snapshots). Jobs survive restarts: leases expire and jobs are re-queued |
| PostgreSQL (`postgresql-x64-17`) | `NT AUTHORITY\NetworkService` | Bundled server, localhost only |

Both Zentriq services depend on the bundled PostgreSQL service. Stopping order for maintenance: API, Worker, PostgreSQL.

## Logs

`C:\ProgramData\Zentriq\Logs`: `api-*.log`, `worker-*.log` (JSON lines, daily files), `provision-*.log` (setup/repair),
`backup-*.log`, `maintenance-*.log`, `install-result.json` (last setup outcome). Secrets are redacted before writing.
Setup logs: `%TEMP%\Zentriq-Setup_*.log` (bootstrapper and MSI), `%TEMP%\install-postgresql.log` (PostgreSQL installer).

## Replacing the HTTPS certificate

1. Import the certificate (with private key) into *Local Computer › Personal* and note its thumbprint.
2. Run `Zentriq-Setup-x64.exe` → *Repair* is not enough to change settings; instead edit
   `C:\ProgramData\Zentriq\Config\zentriq.json`: set `"Https": { "CertificateThumbprint": "<thumbprint>" }`,
   then run `zentriqctl repair` (it validates the certificate and grants the API service read access to its private key).
3. To change the host name or port, edit `HostName`/`Port` in the same file, update the firewall rule
   (`Get-NetFirewallRule -DisplayName "Zentriq Console*"`), and run `zentriqctl repair`.

Self-signed certificate: setup adds it to *Trusted Root* on the server only. To avoid browser warnings elsewhere,
distribute `C:\ProgramData\Zentriq\Secrets\https.pfx`'s public certificate via Group Policy or replace it as above.

## Company profile

*System Settings › Company profile* holds the customer's company name, legal name, address, tax ID and primary
contact. Administrators (`settings.manage`) can edit it; changes are audited. When a production license that carries
licensee details is imported, empty profile fields are filled from it and *Licensing* shows the signed licensee.

## Discovery credentials, Active Directory and agents

Scan credentials, Active Directory discovery and the Zentriq Agent are described in
[agents-and-agentless.md](agents-and-agentless.md), including the limitations of agentless collection.

**Scan history.** Click a scan's name to see the result for every device (open ports, how inventory was collected,
or why it was not). Finished scans can be deleted one by one, by selection, or with *Delete old scans…* (older than
N days). Deleting a scan removes its results and log only; devices it found stay in the asset inventory. A running
scan must be cancelled first.

## Asset inventory: collect now, addresses, services

* **Collect inventory now** (Asset Inventory, for selected assets or all; also on each asset and on the Dashboard):
  assets with an agent report within about 2 minutes (agents check in every 2 minutes); assets without an agent are
  collected agentlessly by a scan that uses every stored inventory credential (each still limited to its allowed
  networks and the approved scan scopes).
* **Addresses.** The inventory shows IPv4 addresses. To also show IPv6, tick *IPv6* on the Asset Inventory toolbar or
  *System Settings › Sign-in security › Show IPv6 addresses* (administrators).
* **Logged-on user** comes from the latest inventory (console user reported by Windows).
* **Type.** Laptop / desktop / physical server from the chassis; any virtual machine (Hyper-V, VMware, VirtualBox,
  KVM/QEMU, Xen, cloud VMs) is shown as *Virtual machine*, even when the hypervisor emulates a laptop chassis or battery.
* **Services: start, stop, restart** (asset › Services; permission `assets.control`, Administrators by default).
  On a device with the Zentriq Agent the agent does it at its next check-in — no credential needed. Without an agent,
  Zentriq connects over WinRM (5985) or DCOM/WMI and asks for an administrator account on the device (a stored
  credential or one typed for that action; a typed password is encrypted until the action runs and then deleted).
  Every action is audited and listed under *Recent service actions*. Services that would cut the device off from
  management or destabilise Windows (ZentriqAgent, WinRM, RPC, WMI, LSM, firewall/BFE, Event Log …) cannot be
  stopped from the console.

## Moving data

PostgreSQL data can be placed on another volume at installation. To move the Zentriq data folder later: stop the
services, move the folder (preserving ACLs, e.g. `robocopy /E /COPYALL /MOVE`), set
`HKLM\SOFTWARE\Zentriq\Console\DataDirectory`, run `zentriqctl repair --data-dir <new path>`.

## Uninstalling

*Apps & features › Zentriq › Uninstall* (or `Zentriq-Setup-x64.exe /uninstall /quiet`) removes program
files, services, firewall rule and scheduled tasks. It **does not delete** customer data, backups, secrets, the
certificate or the PostgreSQL server/database, so a later installation continues with the same data and trial.

Complete removal (irreversible – take and export a backup first):
1. Uninstall Zentriq as above.
2. Uninstall *PostgreSQL 17* from *Apps & features* (if it was installed for Zentriq) and delete its data folder.
3. Delete `C:\ProgramData\Zentriq` and `HKLM\SOFTWARE\Zentriq`.
4. Remove the Zentriq self-signed certificate from *Local Computer › Trusted Root*.

## Account and access

* Sign-in sessions: HttpOnly, Secure, SameSite=Strict cookie, 30 minutes sliding. Login attempts are rate limited.
* **Two-step verification (MFA).** Each user turns it on under *avatar › My account & MFA › Set up authenticator app*:
  scan the QR code with **Microsoft Authenticator**, **Google Authenticator** or any TOTP app (or type the key shown),
  then enter the 6-digit code. From then on every sign-in asks for the current code.
  *System Settings › Sign-in security › Require two-step verification* makes it mandatory: users without MFA must set
  it up at their next sign-in and cannot use anything else until they do (the server grants no permissions to such a
  session). Set it up for your own account first.
* **Lost phone.** An administrator opens *Users, Roles & Sites › Edit / Reset MFA* for that user; the user signs in
  with the password and sets MFA up again. Locked-out last administrator: on the server run
  `zentriqctl reset-password --email admin@company.com --disable-mfa` (elevated).
* **Forgotten password.**
  * With e-mail configured (*System Settings › E-mail (SMTP)*: server, port 587 STARTTLS or 25, optional user/password,
    sender, console address used in links; *Send test*), the sign-in page offers *Forgot password?*. The user receives
    a one-time link valid for 30 minutes (configurable). Users with MFA must also enter an authenticator code to
    reset. The answer never reveals whether an address has an account; at most three links per user per hour.
  * Without e-mail, an administrator sets a new password under *Users, Roles & Sites › Edit*, or on the server:
    `zentriqctl reset-password --email user@company.com` (prompts for the new password; unlocks the account and signs
    out existing sessions).
* RBAC is enforced by the API for every endpoint; the UI only hides what the API would refuse.
* Every security-relevant action is written to the audit log (`/api/v1/audit`).
