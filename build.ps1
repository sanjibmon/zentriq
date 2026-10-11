#Requires -Version 7.2
<#
.SYNOPSIS
  Restores dependencies and builds Zentriq (frontend + backend). Run from any directory.

.EXAMPLE
  ./build.ps1                       # Release build, version from version.txt
  ./build.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Version,
    [switch] $SkipFrontend
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
. (Join-Path $root 'deployment/scripts/common.ps1')

$Version = Resolve-ZentriqVersion $Version
Write-Section "Zentriq build $Version ($Configuration)"

Assert-Tool dotnet 'Install the .NET SDK pinned in global.json (https://dotnet.microsoft.com/download).'
Invoke-Native dotnet --info | Select-String -Pattern '^\s*(Version|OS Name|OS Version|RID):' | ForEach-Object { Write-Host $_.Line.Trim() }

# ---- frontend ------------------------------------------------------------------------------
$frontend = Join-Path $root 'frontend'
if (-not $SkipFrontend -and (Test-Path (Join-Path $frontend 'package.json'))) {
    Assert-Tool npm 'Install Node.js 22 LTS (https://nodejs.org).'
    Push-Location $frontend
    try {
        Write-Section 'Frontend: npm ci'
        if (Test-Path 'package-lock.json') { Invoke-Native npm ci --no-audit --no-fund } else { Invoke-Native npm install --no-audit --no-fund }
        Write-Section 'Frontend: build'
        Invoke-Native npm run build
    } finally { Pop-Location }
} elseif (-not $SkipFrontend) {
    Write-Warning 'frontend/package.json not found: the API will serve the placeholder page in backend/Zentriq.Api/wwwroot.'
}

# ---- backend -------------------------------------------------------------------------------
$sln = Join-Path $root 'backend/Zentriq.slnx'
Write-Section 'Backend: restore'
Invoke-Native dotnet restore $sln
Write-Section 'Backend: build'
Invoke-Native dotnet build $sln --configuration $Configuration --no-restore "-p:Version=$Version" '-warnaserror:NU1901;NU1902;NU1903;NU1904'

Write-Section 'Vulnerable package check'
$audit = Invoke-Native dotnet list $sln package --vulnerable --include-transitive 2>&1
$audit | Write-Host
if ($audit -match 'has the following vulnerable packages') { throw 'Vulnerable NuGet packages found (see above).' }

Write-Host "`nBuild succeeded: Zentriq $Version" -ForegroundColor Green
