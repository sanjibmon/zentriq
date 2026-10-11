# Upgrading Zentriq

1. Read the release notes and check `release-manifest.json`/`SHA256SUMS.txt` of the new version.
2. Optional but recommended: `zentriqctl backup --label before-upgrade` and copy the backup off the server.
3. Run the new `Zentriq-Setup-x64.exe` (interactive or `/quiet`). Setup detects the existing installation
   (Windows Installer major upgrade) and the existing data, skips the configuration pages and reuses the saved
   database and HTTPS settings.

What happens:

* Services are stopped, program files replaced. The data folder, secrets, certificates and the database are not touched by file operations.
* `zentriqctl` runs in **upgrade** mode: it records the installation history, **takes an encrypted pre-upgrade
  backup if the database schema will change**, applies pending migrations (each in its own transaction, under an
  advisory lock, with checksums of already-applied migrations verified), restarts the services and repeats every
  health check (API readiness, worker self-test job, trial state, console over HTTPS).
* The trial and license state are unchanged by upgrades.
* Downgrades are blocked by the installer; a database created by a newer version is refused by an older build.

## If an upgrade fails

Setup shows the failed step and log path and rolls back the program files to the previous version. If migrations
had already been applied, the previous version will refuse to start against the newer schema. Then either:

* fix the cause (see the log) and run the **new** setup again – it continues from where it stopped; or
* roll back completely: reinstall the previous version and restore the pre-upgrade backup
  (`zentriqctl restore <Backups\...pre-upgrade....zbak> --yes`), see [backup-restore.md](backup-restore.md).

Version history: `zentriq.installation_history` and `zentriq.schema_migrations` tables, and `Logs\provision-*.log`.
