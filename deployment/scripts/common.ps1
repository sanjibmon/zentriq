# Shared helpers for build.ps1, test.ps1 and package-windows.ps1 (dot-sourced).

function Write-Section([string] $Text) {
    Write-Host ''
    Write-Host "==== $Text ====" -ForegroundColor Cyan
}

function Assert-Tool([string] $Name, [string] $Hint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "$Name was not found on PATH. $Hint" }
}

# Runs a native command and throws on a non-zero exit code (PowerShell does not by default).
# Deliberately a simple function (no param block) so arguments such as -p:Name=Value or -engine are
# passed to the native tool unchanged instead of being bound as PowerShell parameters.
function Invoke-Native {
    $command = $args[0]
    $rest = if ($args.Count -gt 1) { $args[1..($args.Count - 1)] } else { @() }
    & $command @rest
    if ($LASTEXITCODE -ne 0) { throw "'$command $($rest -join ' ')' failed with exit code $LASTEXITCODE." }
}

# Make the reason for a failure visible in redirected/captured output (CI logs).
function Write-Failure($ErrorRecord) {
    Write-Host ("ERROR: " + ($ErrorRecord | Out-String)) -ForegroundColor Red
    if ($ErrorRecord.ScriptStackTrace) { Write-Host $ErrorRecord.ScriptStackTrace }
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
