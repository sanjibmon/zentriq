# Backup and restore

## What a backup contains

`zentriqctl backup` writes `C:\ProgramData\Zentriq\Backups\zentriq-<label>-<timestamp>.zbak`, containing:

* a `pg_dump` (custom format) of the Zentriq database,
* `Config\zentriq.json`,
* the installation secrets the data depends on (install secret for the trial integrity check, backup key) –
  stored **only inside the encryption**,
* a manifest (product version, schema version, time, machine).

The archive is encrypted with AES-256-GCM in authenticated 1 MiB chunks using the installation's backup key. The
header (unencrypted) only holds metadata and the key fingerprint. Program files are not included (reinstall them
from the installer of the same or newer version).

## Recovery key – keep it separate

At first installation setup writes `Secrets\backup-recovery-key.txt`. **Move it to offline storage** (password
manager or safe) and delete it from the server. Restoring onto a *different* server requires this key. Losing both
the server and the key means backups cannot be decrypted.

## Schedules

* `\Zentriq\Nightly backup` scheduled task at 01:30, keeps the 14 most recent nightly backups.
* `POST /api/v1/system/backups` (*Settings › Backup* in the console) queues a `maintenance.backup` job that the privileged
  `\Zentriq\Maintenance` task runs within 5 minutes (the web process never has access to the backup key).
* Copy backups off the server (e.g. a scheduled `robocopy` of the `Backups` folder to a file share).

## Verify (restore validation)

```powershell
zentriqctl backup-verify <file.zbak>            # decrypts and checks integrity of every chunk
zentriqctl backup-verify <file.zbak> --deep     # also restores into a temporary database, checks the schema, then drops it
zentriqctl backup-verify <file.zbak> --recovery-key E:\keys\backup-recovery-key.txt
```
Run `--deep` monthly (the CI acceptance suite runs it on every build).

## Restore on the same server

```powershell
zentriqctl restore C:\ProgramData\Zentriq\Backups\zentriq-nightly-20261011-013000.zbak --yes
zentriqctl verify
```
Restore stops the services, takes a safety backup of the current data, replaces the database in a single
transaction (left unchanged if it fails), restores the install secret and backup key, re-applies migrations and
grants, and restarts the services.

## Restore on a new server (disaster recovery)

1. Install the same or a newer Zentriq version on the new server (any administrator details; they are replaced).
2. Copy the `.zbak` file and the recovery key file to the server.
3. `zentriqctl restore <file.zbak> --recovery-key <backup-recovery-key.txt> --yes`
4. `zentriqctl verify`, then sign in with an administrator account from the restored data.
5. The license is bound to the installation ID in the restored data; if Zentriq reports a binding problem, export a
   new activation request and contact Zentriq for a replacement license.
