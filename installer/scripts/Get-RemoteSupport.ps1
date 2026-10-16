#Requires -Version 7.2
<#
.SYNOPSIS
  Stages the Zentriq Remote Support engine: Node.js (Windows x64) and MeshCentral with its Windows modules.

.DESCRIPTION
  Produces <OutDir>\node\node.exe, <OutDir>\app\node_modules\meshcentral\…, <OutDir>\branding\* and
  <OutDir>\remote-support.lock.json. Node.js is the newest release of the major version in -NodeMajor (or exactly
  -NodeVersion); the zip is checked against nodejs.org's SHASUMS256.txt and node.exe must carry a valid Authenticode
  signature from the OpenJS Foundation. MeshCentral and its modules are installed from npm with the exact versions in
  installer/remote/package.json (no install scripts run). The server never downloads anything at run time.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [int] $NodeMajor = 22,
    [string] $NodeVersion,
    [string] $CacheDir = (Join-Path $PSScriptRoot '..\..\artifacts\prereqs')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'
$remoteSrc = Join-Path $PSScriptRoot '..\remote'

Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutDir, $CacheDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# ---- Node.js ------------------------------------------------------------------------------------------------
if (-not $NodeVersion) {
    $index = Invoke-RestMethod 'https://nodejs.org/dist/index.json'
    $NodeVersion = ($index | Where-Object { $_.version -like "v$NodeMajor.*" -and $_.files -contains 'win-x64-zip' } | Select-Object -First 1).version
    if (-not $NodeVersion) { throw "No Node.js $NodeMajor release for win-x64 found." }
}
if (-not $NodeVersion.StartsWith('v')) { $NodeVersion = "v$NodeVersion" }
$zipName = "node-$NodeVersion-win-x64.zip"
$zip = Join-Path $CacheDir $zipName
$base = "https://nodejs.org/dist/$NodeVersion"
$sums = Invoke-RestMethod "$base/SHASUMS256.txt"
$expected = (($sums -split "`n") | Where-Object { $_ -match "\s$([regex]::Escape($zipName))$" } | Select-Object -First 1) -replace '\s.*$', ''
if (-not $expected) { throw "$zipName is not listed in $base/SHASUMS256.txt." }
if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $expected.ToUpperInvariant()) {
    Write-Host "Downloading Node.js $NodeVersion"
    Invoke-WebRequest "$base/$zipName" -OutFile $zip -UseBasicParsing
}
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($actual -ne $expected.ToUpperInvariant()) { Remove-Item $zip -Force; throw "${zipName}: SHA-256 $actual does not match nodejs.org ($expected)." }
$tmp = Join-Path $CacheDir "node-extract"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive $zip -DestinationPath $tmp
$nodeSrc = Join-Path $tmp "node-$NodeVersion-win-x64"
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir 'node') | Out-Null
Copy-Item (Join-Path $nodeSrc 'node.exe'), (Join-Path $nodeSrc 'LICENSE') (Join-Path $OutDir 'node')
$sig = Get-AuthenticodeSignature (Join-Path $OutDir 'node\node.exe')
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike '*OpenJS Foundation*') { throw "node.exe signature: $($sig.Status) $($sig.SignerCertificate.Subject)" }
Write-Host "Node.js $NodeVersion verified (SHA-256 $actual, signed by OpenJS Foundation)."

# ---- MeshCentral --------------------------------------------------------------------------------------------
$app = Join-Path $OutDir 'app'
New-Item -ItemType Directory -Force -Path $app | Out-Null
Copy-Item (Join-Path $remoteSrc 'package.json') $app
Push-Location $app
try {
    & npm install --omit=dev --no-audit --no-fund --ignore-scripts --no-package-lock --loglevel=error
    if ($LASTEXITCODE -ne 0) { throw "npm install failed ($LASTEXITCODE)." }
} finally { Pop-Location }
$mc = Get-Content (Join-Path $app 'node_modules\meshcentral\package.json') -Raw | ConvertFrom-Json
$want = (Get-Content (Join-Path $remoteSrc 'package.json') -Raw | ConvertFrom-Json).dependencies.meshcentral
if ($mc.version -ne $want) { throw "MeshCentral $($mc.version) installed, expected $want." }
# The server checks its modules at start and would try to download missing ones: prove they are all present.
& (Join-Path $OutDir 'node\node.exe') -e @"
const need=['archiver','cbor','compression','cookie-session','express','express-handlebars','express-ws','ipcheck','minimist','multiparty','@seald-io/nedb','node-forge','ua-parser-js','ua-client-hints-js','ws','yauzl','@zip.js/zip.js','node-windows','loadavg-windows'];
const path=require('path'); const base=path.join(process.cwd(),'node_modules');
const missing=need.filter(m=>{try{require.resolve(path.join(base,m,'package.json'));return false}catch(e){return true}});
if(missing.length){console.error('Missing modules: '+missing.join(', '));process.exit(1)} console.log('All MeshCentral modules present.');
"@
if ($LASTEXITCODE -ne 0) { throw 'MeshCentral modules are incomplete.' }
# Remove documentation and source maps (not used at run time) to keep the installer smaller.
Get-ChildItem (Join-Path $app 'node_modules') -Recurse -File -Include '*.md', '*.map' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notmatch '^(LICENSE|LICENCE|NOTICE)' } | Remove-Item -Force

# ---- branding -------------------------------------------------------------------------------------------------
$branding = Join-Path $OutDir 'branding'
New-Item -ItemType Directory -Force -Path $branding | Out-Null
Copy-Item (Join-Path $remoteSrc 'remote-title.png'), (Join-Path $remoteSrc 'remote-agent.png'), (Join-Path $remoteSrc 'remote-agent.ico') $branding

$files = (Get-ChildItem $OutDir -Recurse -File).Count
[ordered]@{
    node        = [ordered]@{ version = $NodeVersion; sha256 = $actual.ToLowerInvariant(); signer = $sig.SignerCertificate.Subject; license = 'MIT' }
    meshcentral = [ordered]@{ version = $mc.version; license = 'Apache-2.0'; source = 'https://github.com/Ylianst/MeshCentral' }
    files       = $files
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'remote-support.lock.json') -Encoding UTF8
Write-Host "Remote support staged: MeshCentral $($mc.version), Node.js $NodeVersion, $files files."
