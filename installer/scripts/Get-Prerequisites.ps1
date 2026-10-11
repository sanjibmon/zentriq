#Requires -Version 7.2
<#
.SYNOPSIS
  Downloads and verifies the third-party installers bundled into Zentriq-Setup-x64.exe.

.DESCRIPTION
  For each entry in installer/prereqs.json:
    * reuses an existing file in -OutDir (offline builds: copy the files there beforehand), or downloads it;
    * verifies the Authenticode signature is valid and issued to the expected publisher;
    * verifies the SHA-256 hash when one is pinned in prereqs.json (fails on mismatch);
    * writes prereqs.lock.json with the observed hash, signer, file version and size.
  Nothing unverified is ever passed to the installer build.
#>
[CmdletBinding()]
param(
    [string] $Manifest = (Join-Path $PSScriptRoot '..\prereqs.json'),
    [string] $OutDir = (Join-Path $PSScriptRoot '..\..\artifacts\prereqs'),
    [switch] $Offline
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much faster without the progress bar

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$spec = Get-Content $Manifest -Raw | ConvertFrom-Json
$lock = [ordered]@{}

foreach ($name in @('postgresql', 'vcredist')) {
    $p = $spec.$name
    $target = Join-Path $OutDir $p.file
    if (-not (Test-Path $target)) {
        if ($Offline) { throw "$($p.file) is missing from $OutDir and -Offline was specified." }
        $ok = $false
        foreach ($url in $p.urls) {
            try {
                Write-Host "Downloading $($p.product) from $url"
                $tmp = "$target.partial"
                Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -MaximumRedirection 10
                Move-Item $tmp $target -Force
                $ok = $true
                break
            } catch {
                Write-Warning "Download failed from ${url}: $($_.Exception.Message)"
                Remove-Item "$target.partial" -ErrorAction SilentlyContinue
            }
        }
        if (-not $ok) { throw "Could not download $($p.file). Place a verified copy in $OutDir and re-run with -Offline." }
    }

    $sig = Get-AuthenticodeSignature -FilePath $target
    $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '' }
    if ($sig.Status -ne 'Valid') {
        Remove-Item $target -Force
        throw "$($p.file): Authenticode signature status is '$($sig.Status)' ($($sig.StatusMessage)). File deleted."
    }
    if ($subject -notlike "*$($p.publisher)*") {
        Remove-Item $target -Force
        throw "$($p.file): signed by '$subject', expected publisher '$($p.publisher)'. File deleted."
    }

    $hash = (Get-FileHash -Algorithm SHA256 -Path $target).Hash.ToLowerInvariant()
    if ($p.sha256 -and $p.sha256.Trim() -ne '' -and $hash -ne $p.sha256.ToLowerInvariant()) {
        throw "$($p.file): SHA-256 $hash does not match the pinned value $($p.sha256) in prereqs.json."
    }
    if (-not $p.sha256) {
        Write-Warning "$($p.file): no SHA-256 pinned in prereqs.json. Observed $hash (signature verified). Pin it to make builds reproducible."
    }

    $info = (Get-Item $target).VersionInfo
    $lock[$name] = [ordered]@{
        file        = $p.file
        product     = $p.product
        version     = $p.version
        fileVersion = $info.FileVersion
        productVersion = $info.ProductVersion
        sha256      = $hash
        pinned      = [bool]$p.sha256
        signer      = $subject
        signerThumbprint = $sig.SignerCertificate.Thumbprint
        timestamped = [bool]$sig.TimeStamperCertificate
        bytes       = (Get-Item $target).Length
        license     = $p.license
    }
    Write-Host ("OK  {0}  {1}  {2}" -f $p.file, $hash, $subject)
}

$lockPath = Join-Path $OutDir 'prereqs.lock.json'
$lock | ConvertTo-Json -Depth 5 | Set-Content -Path $lockPath -Encoding UTF8
Write-Host "Wrote $lockPath"
