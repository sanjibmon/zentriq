# Upgrading Zentriq

1. Read the release notes and check `release-manifest.json`/`SHA256SUMS.txt` of the new version.
2. Optional but recommended: `zentriqctl backup --label before-upgrade` and copy the backup off the server.
3. Run the new `Zentriq-Setup-x64.exe` (interactive or `/quiet`). Setup detects the existing installation
   (Windows Installer major upgrade) and the existing data and **updates it in place**: the wizard is
   *Welcome → Update Zentriq → Ready*, without the licence, folder, database, HTTPS or administrator pages, and the
   saved database and HTTPS settings are reused. Afterwards there is still exactly one *Zentriq* entry in
   Apps and Features, showing the new version.

## Versions and builds

Every package carries a four-part version `major.minor.patch.build`, for example `1.1.1.31` for build 31 of
1.1.1 (the build number is the CI run number, also in the release name `v1.1.1-build.31` and in
`release-manifest.json` → `packageVersion`). A newer build of the same version is therefore a newer product
version, and installing it **updates** the existing installation – it does not install a second copy. This also
applies to installations of earlier 1.1.1 builds made before the build number was added (they report `1.1.1`).

Windows Installer itself compares only the first three parts. Install the newest build: running an *older* build
of the same version over a newer one is not refused by `Zentriq.msi` and would replace the program files with the
older build (the database is never downgraded – an older build refuses a schema created by a newer one).

## Agents

Install the new `ZentriqAgent-x64.msi` over the existing agent (`msiexec /i ZentriqAgent-x64.msi /qn`, or let
your GPO/Intune/SCCM deployment assign the new package). It updates the agent in place: **no enrollment code is
needed**, the device keeps its registration and certificate, its history stays on the same asset, and the console
shows the new agent version after the next check-in. The interactive wizard skips the licence and enrollment pages
when an agent is already installed. Agents of the previous version keep working with the upgraded server, so
servers and agents can be updated at different times.

What happens:

* Services are stopped, program files replaced. The data folder, secrets, certificates and the database are not touched by file operations.
* `zentriqctl` runs in **upgrade** mode: it records the installation history, **takes an encrypted pre-upgrade
  backup if the database schema will change**, applies pending migrations (each in its own transaction, under an
  advisory lock, with checksums of already-applied migrations verified), restarts the services and repeats every
  health check (API readiness, worker self-test job, trial state, console over HTTPS).
* The trial and license state are unchanged by upgrades.
* Downgrades to an earlier *version* (e.g. 1.1.2 → 1.1.1) are blocked by the installer; a database created by a newer version is refused by an older build. See *Versions and builds* for older builds of the same version.

## If an upgrade fails

Setup shows the failed step and log path and rolls back the program files to the previous version. If migrations
had already been applied, the previous version will refuse to start against the newer schema. Then either:

* fix the cause (see the log) and run the **new** setup again – it continues from where it stopped; or
* roll back completely: reinstall the previous version and restore the pre-upgrade backup
  (`zentriqctl restore <Backups\...pre-upgrade....zbak> --yes`), see [backup-restore.md](backup-restore.md).

Version history: `zentriq.installation_history` and `zentriq.schema_migrations` tables, and `Logs\provision-*.log`.
