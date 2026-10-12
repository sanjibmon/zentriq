#Requires -Version 7.2
<#
.SYNOPSIS
  Builds the Windows release: Zentriq.msi, Zentriq-Setup-x64.exe, the offline MSI bundle,
  checksums and a release manifest, in artifacts/release.

.DESCRIPTION
  Steps (each fails the build on error):
    1. Publish API, Worker and zentriqctl (self-contained win-x64; no .NET runtime needed on the server).
    2. Stage the web console (frontend/dist), documentation and third-party notices.
    3. Download + verify prerequisites (PostgreSQL, VC++ runtime): Authenticode publisher and pinned SHA-256.
    4. Build the Zentriq Agent and ZentriqAgent-x64.msi (served by the console), the MSI custom actions,
       then Zentriq.msi (WiX 5.0.2, ICE validation on).
    5. Build Zentriq-Setup-x64.exe (Burn) with the MSI, PostgreSQL and VC++ runtime embedded.
    6. Optionally Authenticode-sign binaries, MSI and bundle (engine detach/reattach) and verify signatures.
    7. Write SHA256SUMS.txt and release-manifest.json.

  Must run on Windows (WiX/ICE validation needs Windows Installer).

.PARAMETER SignCertThumbprint
  Thumbprint of a code-signing certificate in Cert:\CurrentUser\My or Cert:\LocalMachine\My. When omitted the
  artifacts are unsigned and the manifest says so.

.EXAMPLE
  ./package-windows.ps1 -Version 1.0.0
  ./package-windows.ps1 -Version 1.0.0 -SignCertThumbprint ABCD... -TimestampUrl http://timestamp.digicert.com
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Configuration = 'Release',
    [switch] $SkipPublish,
    [switch] $OfflinePrereqs,
    [string] $SignCertThumbprint = $env:ZENTRIQ_SIGN_THUMBPRINT,
    [string] $TimestampUrl = $(if ($env:ZENTRIQ_TIMESTAMP_URL) { $env:ZENTRIQ_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' })
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
. (Join-Path $root 'deployment/scripts/common.ps1')
trap { Write-Failure $_; break }
if (-not $IsWindows) { throw 'package-windows.ps1 must run on Windows (WiX and MSI validation require Windows Installer).' }

$Version = Resolve-ZentriqVersion $Version
$art = Join-Path $root 'artifacts'
$staging = Join-Path $art 'staging'
$prereqDir = Join-Path $art 'prereqs'
$release = Join-Path $art 'release'
$wixVersion = '5.0.2'
Write-Section "Packaging Zentriq $Version"
Assert-Tool dotnet 'Install the .NET SDK pinned in global.json.'

# ---- 1. publish ------------------------------------------------------------------------------
if (-not $SkipPublish) {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    $publish = @(
        @{ Project = 'backend/Zentriq.Api/Zentriq.Api.csproj';       Out = 'api' },
        @{ Project = 'backend/Zentriq.Worker/Zentriq.Worker.csproj'; Out = 'worker' },
        @{ Project = 'backend/Zentriq.Setup/Zentriq.Setup.csproj';   Out = 'tools' }
    )
    foreach ($p in $publish) {
        Write-Section "Publish $($p.Project)"
        Invoke-Native { dotnet publish (Join-Path $root $p.Project) --configuration $Configuration --runtime win-x64 --self-contained true `
            "-p:Version=$Version" '-p:PublishReadyToRun=false' '-p:DebugType=none' '-p:GenerateDocumentationFile=false' `
            --output (Join-Path $staging $p.Out) }
    }
    # Development settings never ship.
    Get-ChildItem $staging -Recurse -Filter 'appsettings.Development.json' | Remove-Item -Force
}

# ---- 2. web console, docs, notices -------------------------------------------------------------
$dist = Join-Path $root 'frontend/dist'
$wwwroot = Join-Path $staging 'api/wwwroot'
if (Test-Path (Join-Path $dist 'index.html')) {
    Write-Section 'Stage web console (frontend/dist)'
    Remove-Item $wwwroot -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item $dist $wwwroot -Recurse
} else {
    throw 'frontend/dist/index.html not found: the web console has not been built. Run ./build.ps1 first (needs Node.js 22).'
}
$docs = Join-Path $staging 'docs'
New-Item -ItemType Directory -Force -Path $docs | Out-Null
Copy-Item (Join-Path $root 'docs/*.md') $docs -Force
Copy-Item (Join-Path $root 'installer/assets/THIRD-PARTY-NOTICES.txt') $docs -Force

# ---- 3. prerequisites --------------------------------------------------------------------------
Write-Section 'Prerequisites (download + verify)'
& (Join-Path $root 'installer/scripts/Get-Prerequisites.ps1') -OutDir $prereqDir -Offline:$OfflinePrereqs
$lock = Get-Content (Join-Path $prereqDir 'prereqs.lock.json') -Raw | ConvertFrom-Json
$pgFile = $lock.postgresql.file
$vcBuild = [int]($lock.vcredist.fileVersion -split '\.')[2]
Write-Host "PostgreSQL installer: $pgFile; VC++ runtime build: $vcBuild"

# ---- 6a. signing helpers -----------------------------------------------------------------------
# Two supported ways to sign (see docs/code-signing.md):
#   certstore : certificate in the Windows certificate store / USB token  -> -SignCertThumbprint (ZENTRIQ_SIGN_THUMBPRINT)
#   akv       : certificate whose key lives in Azure Key Vault (cloud HSM) -> ZENTRIQ_AKV_URL, ZENTRIQ_AKV_CERT,
#               AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET; signs with AzureSignTool (works in CI)
$signMode = if ($env:ZENTRIQ_AKV_URL -and $env:ZENTRIQ_AKV_CERT) { 'akv' } elseif ($SignCertThumbprint) { 'certstore' } else { $null }
$signtool = $null
if ($signMode -eq 'certstore') {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK signing tools).' }
} elseif ($signMode -eq 'akv') {
    foreach ($v in 'AZURE_TENANT_ID', 'AZURE_CLIENT_ID', 'AZURE_CLIENT_SECRET') { if (-not (Get-Item "env:$v" -ErrorAction SilentlyContinue)) { throw "$v must be set for Azure Key Vault signing." } }
    Invoke-Native { dotnet tool update --global AzureSignTool --version 7.0.1 }
}
if ($signMode) { Write-Host "Code signing: $signMode" } else { Write-Warning 'Code signing: not configured (artifacts will show "Unknown publisher").' }
$signDescription = 'Zentriq Console'
$signUrl = 'https://zentriq.example'
function Sign-Files([string[]] $Files) {
    if (-not $signMode -or $Files.Count -eq 0) { return }
    $batches = $Files | ForEach-Object -Begin { $i = 0 } -Process { [pscustomobject]@{ G = [math]::Floor($i++ / 40); F = $_ } } | Group-Object G
    foreach ($batch in $batches) {
        $f = @($batch.Group.F)
        if ($signMode -eq 'certstore') {
            Invoke-Native { & $signtool sign /sha1 $SignCertThumbprint /fd SHA256 /td SHA256 /tr $TimestampUrl /d $signDescription /du $signUrl @f }
        } else {
            Invoke-Native { AzureSignTool sign -kvu $env:ZENTRIQ_AKV_URL -kvc $env:ZENTRIQ_AKV_CERT -kvt $env:AZURE_TENANT_ID -kvi $env:AZURE_CLIENT_ID -kvs $env:AZURE_CLIENT_SECRET -tr $TimestampUrl -td sha256 -fd sha256 -d $signDescription -du $signUrl -v @f }
        }
    }
    foreach ($file in $Files) {
        $sig = Get-AuthenticodeSignature $file
        if ($sig.Status -ne 'Valid') { throw "Signature check failed for ${file}: $($sig.Status) $($sig.StatusMessage)" }
    }
}

if ($signMode) {
    Write-Section 'Sign Zentriq binaries'
    $ours = Get-ChildItem $staging -Recurse -Include 'Zentriq*.dll', 'Zentriq*.exe', 'zentriqctl.exe', 'zentriqctl.dll' | Select-Object -ExpandProperty FullName
    Sign-Files $ours
}

# ---- 4a. Zentriq Agent (.NET Framework 4.8 service) + ZentriqAgent-x64.msi ----------------------
Write-Section 'Zentriq Agent'
$agentStage = Join-Path $art 'staging-agent'
Remove-Item $agentStage -Recurse -Force -ErrorAction SilentlyContinue
Invoke-Native { dotnet publish (Join-Path $root 'agent/Zentriq.Agent/Zentriq.Agent.csproj') --configuration $Configuration `
    "-p:Version=$Version" '-p:DebugType=none' --output $agentStage }
Get-ChildItem $agentStage -Include '*.pdb', '*.xml' -Recurse | Remove-Item -Force
if (-not (Test-Path (Join-Path $agentStage 'ZentriqAgent.exe'))) { throw 'ZentriqAgent.exe was not produced.' }
Sign-Files @(Get-ChildItem $agentStage -Include 'ZentriqAgent.exe', 'Zentriq*.dll' -Recurse | Select-Object -ExpandProperty FullName)
Remove-Item (Join-Path $art 'agent-msi') -Recurse -Force -ErrorAction SilentlyContinue
Invoke-Native { dotnet build (Join-Path $root 'installer/agent-msi/Zentriq.AgentMsi.wixproj') --configuration $Configuration `
    "-p:ProductVersion=$Version" "-p:AgentDir=$agentStage" }
$agentMsi = Join-Path $art 'agent-msi/ZentriqAgent-x64.msi'
if (-not (Test-Path $agentMsi)) { throw "Agent MSI not found at $agentMsi" }
Sign-Files @($agentMsi)
# The console serves the agent package at https://<server>/downloads/ZentriqAgent-x64.msi
$downloads = Join-Path $wwwroot 'downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
Copy-Item $agentMsi $downloads -Force
Set-Content (Join-Path $downloads 'ZentriqAgent-x64.version') $Version -Encoding ascii -NoNewline

# ---- 4. custom actions + MSI -------------------------------------------------------------------
Write-Section 'MSI custom actions'
$caProj = Join-Path $root 'installer/customactions/Zentriq.CustomActions.csproj'
Invoke-Native { dotnet build $caProj --configuration $Configuration "-p:Version=$Version" }
$caDll = Get-ChildItem (Join-Path $root 'installer/customactions/bin') -Recurse -Filter 'Zentriq.CustomActions.CA.dll' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $caDll) { throw 'Zentriq.CustomActions.CA.dll was not produced.' }
$caOut = Join-Path $art 'customactions'
New-Item -ItemType Directory -Force -Path $caOut | Out-Null
Copy-Item $caDll $caOut -Force
$caDll = Join-Path $caOut 'Zentriq.CustomActions.CA.dll'
Sign-Files @($caDll)

Write-Section 'Zentriq.msi'
Remove-Item (Join-Path $art 'msi') -Recurse -Force -ErrorAction SilentlyContinue
Invoke-Native { dotnet build (Join-Path $root 'installer/msi/Zentriq.Msi.wixproj') --configuration $Configuration `
    "-p:ProductVersion=$Version" "-p:StagingDir=$staging" "-p:CustomActionDll=$caDll" }
$msi = Join-Path $art 'msi/Zentriq.msi'
if (-not (Test-Path $msi)) { throw "MSI not found at $msi" }
Sign-Files @($msi)

# ---- 5. bundle -----------------------------------------------------------------------------------
Write-Section 'Zentriq-Setup-x64.exe'
Remove-Item (Join-Path $art 'bundle') -Recurse -Force -ErrorAction SilentlyContinue
Invoke-Native { dotnet build (Join-Path $root 'installer/bootstrapper/Zentriq.Bundle.wixproj') --configuration $Configuration `
    "-p:ProductVersion=$Version" "-p:MsiPath=$msi" "-p:PrereqDir=$prereqDir" "-p:PgInstallerName=$pgFile" "-p:VcRedistMinBuild=$vcBuild" }
$bundle = Join-Path $art 'bundle/Zentriq-Setup-x64.exe'
if (-not (Test-Path $bundle)) { throw "Bundle not found at $bundle" }

if ($signMode) {
    # Burn bundles must have the engine signed first, then the whole bundle.
    Write-Section 'Sign bundle (engine detach/reattach)'
    Invoke-Native { dotnet tool update --global wix --version $wixVersion }
    $engine = Join-Path $art 'bundle/engine.exe'
    Invoke-Native { wix burn detach $bundle '-engine' $engine }
    Sign-Files @($engine)
    Invoke-Native { wix burn reattach $bundle '-engine' $engine '-o' $bundle }
    Sign-Files @($bundle)
    Remove-Item $engine -Force
}

# ---- 7. release folder, offline MSI kit, checksums, manifest -------------------------------------
Write-Section 'Release artifacts'
Remove-Item $release -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $release | Out-Null
Copy-Item $bundle $release
Copy-Item $msi $release
Copy-Item $agentMsi $release

$kit = Join-Path $art 'msi-offline-kit'
Remove-Item $kit -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $kit 'prereqs') | Out-Null
Copy-Item $msi $kit
Copy-Item (Join-Path $prereqDir $pgFile), (Join-Path $prereqDir 'vc_redist.x64.exe'), (Join-Path $prereqDir 'prereqs.lock.json') (Join-Path $kit 'prereqs')
Copy-Item (Join-Path $root 'docs/installation.md') $kit
$kitZip = Join-Path $release "Zentriq-$Version-msi-offline-kit.zip"
Compress-Archive -Path (Join-Path $kit '*') -DestinationPath $kitZip -CompressionLevel Optimal

$docZip = Join-Path $release "Zentriq-$Version-docs.zip"
Compress-Archive -Path (Join-Path $root 'docs/*') -DestinationPath $docZip

$signatures = @{}
foreach ($f in Get-ChildItem $release -File | Where-Object Extension -in '.exe', '.msi') {
    $s = Get-AuthenticodeSignature $f.FullName
    $signatures[$f.Name] = if ($s.Status -eq 'Valid') { "Valid: $($s.SignerCertificate.Subject)" } else { "$($s.Status)" }
    if ($signMode -and $s.Status -ne 'Valid') { throw "Signature verification failed for $($f.Name): $($s.Status)" }
}

$files = foreach ($f in Get-ChildItem $release -File | Sort-Object Name) {
    [ordered]@{ name = $f.Name; bytes = $f.Length; sha256 = Get-Sha256 $f.FullName; signature = $signatures[$f.Name] }
}
($files | ForEach-Object { "$($_.sha256)  $($_.name)" }) | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii

$commit = (git -C $root rev-parse HEAD 2>$null)
$manifest = [ordered]@{
    product        = 'Zentriq Console'
    version        = $Version
    builtAtUtc     = (Get-Date).ToUniversalTime().ToString('o')
    gitCommit      = $commit
    builder        = @{ os = [Environment]::OSVersion.VersionString; dotnetSdk = (dotnet --version); wix = $wixVersion }
    signed         = [bool]$signMode
    signingMode    = $signMode
    files          = $files
    prerequisites  = $lock
    notes          = @(
        'Zentriq-Setup-x64.exe: offline bootstrapper (VC++ runtime + PostgreSQL + Zentriq.msi).',
        'Zentriq.msi: Zentriq only; uses prereqs\ next to the MSI (see the offline kit) or an existing PostgreSQL server.',
        'ZentriqAgent-x64.msi: Zentriq Agent for Windows devices (also served by the console at /downloads).'
    )
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $release 'release-manifest.json') -Encoding UTF8

Get-ChildItem $release | Format-Table Name, Length -AutoSize | Out-String | Write-Host
if (-not $signMode) { Write-Warning 'Artifacts are NOT code-signed (Windows shows "Unknown publisher"). See docs/code-signing.md.' }
Write-Host "Release written to $release" -ForegroundColor Green
