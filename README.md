# Zentriq Console

Self-hosted asset intelligence, endpoint management and IT operations platform for Windows Server.

```
backend/      ASP.NET Core API (Zentriq.Api), Worker service, Application/Domain/Infrastructure, zentriqctl (Zentriq.Setup), tests
database/     versioned PostgreSQL migrations (embedded into the build) and bootstrap
frontend/     web console (React + TypeScript + Vite) — see "Status" below
installer/    WiX 5 MSI (msi/), Burn bootstrapper (bootstrapper/), custom actions, artwork, prerequisite manifest
deployment/   shared build helpers
tests/        installer acceptance suite (installer/), SQL integration assertions (integration/db)
licensing/    license server, signing tool and vendor docs: a separate product, never shipped
docs/         installation, administrator, upgrade, backup/restore, licensing, troubleshooting, security
```

## Build (Windows, PowerShell 7)

Prerequisites: .NET SDK per `global.json`, Node.js 22 (for the web console), internet access for NuGet and the
two third-party installers (or pre-seeded `artifacts/prereqs`).

```powershell
./build.ps1                    # restore + build (fails on vulnerable NuGet packages)
./test.ps1 -PostgresConnection "Host=localhost;Username=postgres;Password=..."   # unit + DB integration tests
./package-windows.ps1          # -> artifacts/release: Zentriq-Setup-x64.exe, Zentriq.msi, offline kit, SHA256SUMS.txt, release-manifest.json
./package-windows.ps1 -SignCertThumbprint <thumbprint>   # Authenticode-sign binaries, MSI and bundle
```

CI: `.github/workflows/windows-release.yml` builds, tests, packages and then installs the produced EXE on a fresh
Windows Server 2022 runner and runs `tests/installer/Invoke-InstallerAcceptance.ps1` (install, verify, restart,
repair, backup + deep restore validation, tampered-license rejection, upgrade, credential-free logs, uninstall with
data preserved, reinstall without trial reset). Installers are published as a GitHub pre-release when everything passes.

Directory layout differences from the original plan: `backend/Zentriq.Setup` (zentriqctl, the provisioning engine run
by the installer) and `installer/customactions` (MSI custom actions) were added; `deployment/configuration` is unused
because configuration is generated per server by zentriqctl.
