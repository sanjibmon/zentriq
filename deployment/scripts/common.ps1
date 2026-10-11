# Shared helpers for build.ps1, test.ps1 and package-windows.ps1 (dot-sourced).

function Write-Section([string] $Text) {
    Write-Host ''
    Write-Host "==== $Text ====" -ForegroundColor Cyan
}

function Assert-Tool([string] $Name, [string] $Hint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "$Name was not found on PATH. $Hint" }
}

# Runs a native command and throws on a non-zero exit code (PowerShell does not by default).
function Invoke-Native {
    param([Parameter(Mandatory, Position = 0)] [string] $Command, [Parameter(ValueFromRemainingArguments)] [object[]] $Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "'$Command $($Arguments -join ' ')' failed with exit code $LASTEXITCODE." }
}

# MSI product versions must be major.minor.build with major/minor < 256 and build < 65536.
function Resolve-ZentriqVersion([string] $Version) {
    if (-not $Version) {
        $file = Join-Path $PSScriptRoot '../../version.txt'
        $Version = (Get-Content $file -Raw).Trim()
    }
    if ($Version -notmatch '^(\d{1,3})\.(\d{1,3})\.(\d{1,5})$' -or [int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or [int]$Matches[3] -gt 65535) {
        throw "Version '$Version' must be major.minor.patch (major, minor <= 255; patch <= 65535)."
    }
    return $Version
}

function Get-Sha256([string] $Path) { (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant() }
