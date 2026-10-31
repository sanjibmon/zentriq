<#
.SYNOPSIS
  Builds Zentriq-LicenseServer-Local-win-x64-<version>.zip: the self-contained license server, a portable PostgreSQL
  (EnterpriseDB binaries, only bin/lib/share) and zls.cmd / zls.ps1.
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
$work = Join-Path ([IO.Path]::GetTempPath()) "zls-local-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$pkg = Join-Path $work 'ZentriqLicenseServer'
New-Item -ItemType Directory -Force -Path $pkg, $OutDir | Out-Null

Write-Host 'Publishing the license server (self-contained, win-x64)…'
dotnet publish (Join-Path $repo 'licensing\Zentriq.LicenseServer\Zentriq.LicenseServer.csproj') -c Release -r win-x64 --self-contained true `
    -o (Join-Path $pkg 'server') "-p:Version=$Version" --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$zipName = "postgresql-$PgVersion-windows-x64-binaries.zip"
$cache = Join-Path ([IO.Path]::GetTempPath()) $zipName
if (-not (Test-Path $cache)) {
    Write-Host "Downloading $zipName…"
    Invoke-WebRequest "https://get.enterprisedb.com/postgresql/$zipName" -OutFile $cache -UseBasicParsing
}
$hash = (Get-FileHash $cache -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "PostgreSQL binaries SHA256: $hash"
if ($PgSha256 -and $hash -ne $PgSha256.ToLowerInvariant()) { throw "PostgreSQL binaries hash mismatch: $hash" }

Write-Host 'Extracting PostgreSQL (bin, lib, share only)…'
$pgTmp = Join-Path $work 'pgzip'
Expand-Archive $cache -DestinationPath $pgTmp
$src = Join-Path $pgTmp 'pgsql'
foreach ($d in 'bin', 'lib', 'share') { Copy-Item (Join-Path $src $d) (Join-Path $pkg "pgsql\$d") -Recurse }
# Leave out pgAdmin, StackBuilder, debug symbols and headers (not needed to run the database).
Get-ChildItem (Join-Path $pkg 'pgsql') -Recurse -Include '*.pdb' | Remove-Item -Force
foreach ($x in 'pgAdmin 4', 'stackbuilder.exe') { Get-ChildItem (Join-Path $pkg 'pgsql\bin') -Filter $x -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force }
foreach ($f in 'LICENSE', 'COPYRIGHT') { if (Test-Path (Join-Path $src $f)) { Copy-Item (Join-Path $src $f) (Join-Path $pkg "pgsql\$f") } }

# Windows PowerShell 5.1 reads BOM-less scripts as ANSI: the scripts must stay plain ASCII.
foreach ($f in 'zls.ps1', 'zls.cmd', 'README.txt') {
    if ([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot $f)) | Where-Object { $_ -gt 127 } | Select-Object -First 1) { throw "$f contains non-ASCII characters" }
}
Copy-Item (Join-Path $PSScriptRoot 'zls.ps1'), (Join-Path $PSScriptRoot 'zls.cmd'), (Join-Path $PSScriptRoot 'README.txt') $pkg
Copy-Item (Join-Path $repo 'docs\license-server.md') $pkg
Set-Content (Join-Path $pkg 'VERSION.txt') "Zentriq License Server (local edition) $Version`r`nPostgreSQL $PgVersion (EnterpriseDB binaries, SHA256 $hash)" -Encoding ascii

$zip = Join-Path $OutDir "Zentriq-LicenseServer-Local-win-x64-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $pkg -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $work -Recurse -Force
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Package: $zip ($mb MB)"
$zip
