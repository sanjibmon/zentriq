<#
.SYNOPSIS
  Builds Zentriq-LicenseServer-Setup-<version>.msi: the self-contained license server (win-x64), a portable
  PostgreSQL 17 (EnterpriseDB binaries: bin, lib, share) and the MSI around them.
.PARAMETER Version
  MSI version (major.minor.build), e.g. 1.1.42.
#>
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $OutDir,
    [string] $PgVersion = '17.11-1',
    # Pin after the first verified download (the build prints the hash).
    [string] $PgSha256 = ''
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$stage = Join-Path ([IO.Path]::GetTempPath()) "zls-setup-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $stage, $OutDir | Out-Null

Write-Host 'Publishing the license server (self-contained, win-x64)...'
dotnet publish (Join-Path $repo 'licensing\Zentriq.LicenseServer\Zentriq.LicenseServer.csproj') -c Release -r win-x64 --self-contained true `
    -o (Join-Path $stage 'server') "-p:Version=$Version" --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Get-ChildItem (Join-Path $stage 'server') -Recurse -Include '*.pdb' | Remove-Item -Force

$zipName = "postgresql-$PgVersion-windows-x64-binaries.zip"
$cache = Join-Path ([IO.Path]::GetTempPath()) $zipName
if (-not (Test-Path $cache)) {
    Write-Host "Downloading $zipName..."
    Invoke-WebRequest "https://get.enterprisedb.com/postgresql/$zipName" -OutFile $cache -UseBasicParsing
}
$hash = (Get-FileHash $cache -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "PostgreSQL binaries SHA256: $hash"
if ($PgSha256 -and $hash -ne $PgSha256.ToLowerInvariant()) { throw "PostgreSQL binaries hash mismatch: $hash" }

Write-Host 'Extracting PostgreSQL (bin, lib, share only)...'
$pgTmp = Join-Path $stage 'pgzip'
Expand-Archive $cache -DestinationPath $pgTmp
$src = Join-Path $pgTmp 'pgsql'
$pg = Join-Path $stage 'pgsql'
foreach ($d in 'bin', 'lib', 'share') { Copy-Item (Join-Path $src $d) (Join-Path $pg $d) -Recurse }
# Leave out pgAdmin, StackBuilder, debug symbols, headers and docs (not needed to run the database).
Get-ChildItem $pg -Recurse -Include '*.pdb' | Remove-Item -Force
Get-ChildItem (Join-Path $pg 'bin') -Filter 'stackbuilder*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$license = Get-ChildItem $src -Recurse -File | Where-Object { $_.Name -match '^(COPYRIGHT|LICENSE)(\.txt)?$' } | Select-Object -First 1
if (-not $license) { throw 'The PostgreSQL license file was not found in the binaries package.' }
Copy-Item $license.FullName (Join-Path $pg 'COPYRIGHT')
# The Visual C++ runtime PostgreSQL needs (app-local copy, as Microsoft allows), for servers without the redistributable.
foreach ($dll in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll') {
    if (-not (Test-Path (Join-Path $pg "bin\$dll"))) {
        $sys = Join-Path $env:SystemRoot "System32\$dll"
        if (Test-Path $sys) { Copy-Item $sys (Join-Path $pg "bin\$dll"); Write-Host "Added $dll (app-local)" }
    }
}
Remove-Item $pgTmp -Recurse -Force

Write-Host 'Building the MSI...'
$msiOut = Join-Path $stage 'msi'
dotnet build (Join-Path $PSScriptRoot 'Zentriq.LicenseServer.Setup.wixproj') -c Release "-p:ProductVersion=$Version" "-p:StageDir=$stage" "-p:OutputPath=$msiOut\" --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed' }
$msi = Get-ChildItem $msiOut -Filter '*.msi' -Recurse | Select-Object -First 1
$target = Join-Path $OutDir "Zentriq-LicenseServer-Setup-$Version.msi"
Copy-Item $msi.FullName $target -Force
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$mb = [math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host "Setup: $target ($mb MB); PostgreSQL $PgVersion (SHA256 $hash)"
$target
