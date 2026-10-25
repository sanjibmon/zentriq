# Backup and restore

Console: **Insight & admin › Backup & Restore** (permission *backup.run*; settings, destinations and restore need
*settings.manage*). Backups and restores run in the privileged `\Zentriq\Maintenance` task (zentriqctl as SYSTEM, every
5 minutes); the web process never reads the installation's backup key.

## What a backup contains (build 85)

A backup is one encrypted file, `zentriq-<server>-<yyyyMMdd-HHmmss>-<label>.zbak`, with everything needed to rebuild the
server:

| Part | Why |
|---|---|
| Database (`pg_dump`, all data) | devices, inventory, settings, users, roles, MFA, audit, licence, trial, reports, lifecycle, cloud … |
| `Config\zentriq.json` | installation settings (for reference; a restore keeps the new server's ports and database settings) |
| Installation secrets | install secret (trial integrity), credential key (stored passwords of scans, destinations, integrations), backup key |
| Console certificate and its local CA (`https.pfx`, `https-ca.pfx`) | agents pin the certificate **key**; the restore brings the same key back and re-issues the certificate for the new server's names |
| Agent CA (`agent-ca.pfx`) | the agents' client certificates keep working |
| Data protection keys (`Secrets\dpkeys`) | MFA secrets; stored without the machine-bound DPAPI layer, so they work on a new server |
| Remote support engine data (`Remote\data`) | remote devices and the engine's agent identity, so installed remote agents reconnect |
| Report files (`Exports`), optional logs (last 30 days) | history |
| Manifest | versions, schema version, contents |

Program files and PostgreSQL itself are not included: the installer provides them.

## Encryption and the backup password

Every backup is encrypted (AES-256-GCM in authenticated 1 MiB chunks; the header line is bound to every chunk, so any
change, truncation or reordering is detected). Format 2 (build 85): a random key encrypts the backup; that key is stored
in the header wrapped once per way to open the backup:

* the **installation backup key** (always) — its copy is the recovery key, `Secrets\backup-recovery-key.txt`;
* the **backup password** (optional, *Backup & Restore › Encryption › Set password*): the key is derived with PBKDF2-SHA256
  (600,000 rounds, random salt). Zentriq stores only that derived key, encrypted with the installation's credential key;
  the password itself is never stored or shown again — keep it in the organisation's password manager.

With a password, a rebuilt server needs only the backup file and the password. Without one, keep the recovery key file
offline (password manager or safe) and delete it from the server — losing both the server and the key means the backups
cannot be opened. Backups made before a password change keep the password they were made with. Backups of earlier builds
(format 1) still open with the recovery key.

## Schedule and retention

* **Schedule** (server time zone): every day, on chosen weekdays, or monthly on a day, at a time. Default: daily at 01:30
  (the `\Zentriq\Nightly backup` task of earlier builds is replaced by this schedule). A server that was off at the
  scheduled time catches up once within 24 hours. Saving the schedule never starts a backup by itself.
* **Back up now** queues a backup; it starts within 5 minutes.
* **Keep on this server**: the newest *n* scheduled and manual backups (default 14; 0 = all). The five newest
  *pre-upgrade* and *pre-restore* safety backups are kept separately. Backups made on the command line with their own
  `--label`, and backups of other servers, are never deleted.
* **Destinations** each keep their own number of backups (default 30).

## Destinations

Each scheduled and manual backup is copied to every enabled destination after it is written. Uploads go to a temporary
name first, so a half-copied file never looks like a backup. A failed copy is retried and then reported (history,
destination status, console notification); the backup on the server is kept.

| Type | Settings | Notes |
|---|---|---|
| Local or attached disk | full folder path (`E:\ZentriqBackups`) | Use another disk than the system drive. The console gets read access to list the folder. |
| Network share (SMB) | `\\server\share\folder`, optional account and password | Without an account the server's computer account (`DOMAIN\SERVER$`) is used. |
| FTP / FTPS | server, port, folder, user, password, FTPS (explicit TLS) or plain, passive mode | The server's TLS certificate must be trusted by Windows. |
| SFTP | server, port, folder, user, password and/or private key (OpenSSH/PEM) with passphrase | The host key is pinned at the first connection; a changed key stops the copy before the credentials are sent. |
| Azure Blob Storage | storage account, container, folder, account key or SAS token, optional endpoint | Block blobs (16 MB blocks). A SAS token needs read, write, delete and list. |
| Amazon S3 / S3-compatible | bucket, region, folder, access key ID, secret key; service URL and path-style for Wasabi, Backblaze B2, MinIO, R2 … | Signature Version 4; multipart above 64 MB. |
| Google Cloud Storage | bucket, folder, service account JSON key | Resumable uploads (16 MB). The service account needs Storage Object Admin on the bucket. |

Passwords, keys and tokens are encrypted with the installation's credential key and never returned by the API. **Test**
writes, lists and deletes a small file (a local folder is only checked: it is written by the maintenance task).

Tips: keep at least one copy off the server; for ransomware protection use storage with soft delete, versioning or
immutability (Azure immutable blobs, S3 Object Lock) and an account that cannot delete older versions.

## History

*Backup & Restore › History* lists every backup: trigger (scheduled, back up now, before an update, before a restore,
command line), result, size, password protection, the copies with their result, and — while the file is still on the
server — **Download** and **Restore…**.

## Restore

Restoring **replaces all current data** with the backup. In the console: **Restore…**, then

1. choose the backup: **upload** a `.zbak` file, one **on this server**, or one **at a destination** (listed from the
   destination itself — after a rebuild, add the destination again first);
2. a backup of another installation (for example the server being replaced) needs its **backup password** or its
   **recovery key** (paste the `Key:` line or the whole file); a backup of this installation opens with its own key;
3. type **RESTORE** to confirm.

The maintenance task then (within 5 minutes): downloads the file if needed, takes a *pre-restore* safety backup, stops
the services, replaces the database in a single transaction (left unchanged if that fails), restores the secrets the data
depends on, the console certificate key and CA (re-issued for this server's names), the agent CA, the data protection
keys (MFA), the remote support data and the report files, applies migrations when the backup is from an older version,
and starts the services. A backup from a **newer** version is refused. The console shows *Restoring…* and then asks to
sign in again — with an account from the backup. The restore is noted in the audit trail, the notifications and on the
Backup & Restore page.

This server's database passwords, ports, host name and installation choices stay as they are.

### Command line

```powershell
zentriqctl backup [--label L] [--keep N]          # back up now (copied to the destinations)
zentriqctl backup-verify <file.zbak> [--deep]     # decrypt and check every chunk; --deep restores into a scratch database
zentriqctl backup-verify <file.zbak> --password   # with the backup password (prompted; or --password-file F)
zentriqctl restore <file.zbak> --password --yes   # restore with the backup password
zentriqctl restore <file.zbak> --recovery-key E:\keys\backup-recovery-key.txt --yes
zentriqctl verify
```

## Disaster recovery (new server)

1. Install the **same or a newer** Zentriq version on the new server (Standard or Advanced; any administrator details —
   they are replaced). Give it the old server's name or IP address if you can, or point the DNS name the agents use to it.
2. Sign in, open **Backup & Restore › Restore…**, upload the latest backup (or add the destination again and pick it there).
3. Enter the backup password (or the old server's recovery key) and confirm.
4. After the restart, sign in with an account from the backup and check *Agents*. Agents reconnect by themselves: the
   restored certificate key is the one they pin and their client certificates chain to the restored agent CA. An agent that
   cannot reach the new address keeps its data and sends it when it can.
5. The licence is bound to the installation ID in the restored data; if Zentriq reports a binding problem, export a new
   activation request and contact Zentriq for a replacement licence.
6. Never run the old and the new server at the same time with the same data (same licence, same agents).

Run `backup-verify --deep` (or a test restore on a spare VM) regularly; the CI acceptance suite backs up, copies to a
folder and an SMB share, verifies with the password and restores from the share on every build.

## API

| Method | Path | |
|---|---|---|
| GET | `/api/v1/backup` | schedule, retention, password state, key fingerprint, destinations (no secrets), running job, last restore |
| PUT | `/backup/settings` | `schedule {enabled, frequency, time, weekdays, day}`, `keepLocal`, `includeExports`, `includeLogs` |
| PUT / DELETE | `/backup/password` | set (`password`, `confirm`) or remove the backup password |
| POST / PUT / DELETE | `/backup/destinations`, `/backup/destinations/{id}` | destinations (`name`, `kind`, `enabled`, `keep`, `settings`, `password`, `privateKey`, `passphrase`) |
| POST | `/backup/destinations/test`, `/backup/destinations/{id}/test` | test (unsaved or saved) |
| GET | `/backup/destinations/{id}/files` | backups at a destination |
| POST | `/backup/run` | back up now |
| GET | `/backup/runs`, `/backup/files`, `/backup/files/{name}` | history; backups on this server; download |
| POST | `/backup/restore/upload` (body = file) · GET `/backup/restore/inspect?file=` | upload a backup / read its header |
| POST | `/backup/restore` | `source` (upload, local, destination), `file`, `destinationId`, `password` or `recoveryKey`, `confirm: "RESTORE"` |
