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

**From the console (agents that support it – installed from build 34 on):** after the server is upgraded, open **Agents**. Agents that
run an older version show *… available*; click **Update** on one agent or **Update N agent(s)** for all. At its next
check-in (within about 2 minutes) each agent downloads `ZentriqAgent-x64.msi` from this server over its mutually
authenticated agent connection (port 8444), checks the SHA-256 checksum the server sent with the request, and installs
it silently (Windows Installer major upgrade). The agent service restarts for a few seconds; the device keeps its
registration, certificate and history. The agent row and the **Activity Log** (type *Agent updates*) show
*Update requested → Updating → Succeeded*, or *Failed* with the reason. The Windows Installer log is on the device in
`C:\ProgramData\Zentriq\Agent\Logs\update-<version>.log`. Older packages are never offered (no downgrade).

**Once, for agents installed before console updates existed (builds up to 33):** those agents cannot download
updates yet, and the Agents page lists them. Install the new MSI over them once – `msiexec /i ZentriqAgent-x64.msi /qn`
or your GPO/Intune/SCCM deployment, **no enrollment code needed**. The agent is updated in place and keeps its
registration and certificate; from then on it updates from the console. Software uninstall from the console also
needs this agent version.

Agents of the previous version keep working with the upgraded server, so servers and agents can be updated at
different times.

## If an upgrade fails

Setup shows the failed step and log path and rolls back the program files to the previous version. If migrations
had already been applied, the previous version will refuse to start against the newer schema. Then either:

* fix the cause (see the log) and run the **new** setup again – it continues from where it stopped; or
* roll back completely: reinstall the previous version and restore the pre-upgrade backup
  (`zentriqctl restore <Backups\...pre-upgrade....zbak> --yes`), see [backup-restore.md](backup-restore.md).

Version history: `zentriq.installation_history` and `zentriq.schema_migrations` tables, and `Logs\provision-*.log`.
