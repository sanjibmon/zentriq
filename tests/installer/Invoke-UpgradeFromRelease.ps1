#Requires -Version 7.2
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Upgrade test from a previously published release: installs the old bundle silently, then runs the new bundle
  over it and verifies the installation still works (services, schema, sign-in, trial kept and moved to the
  30-device policy). Finally removes everything (Zentriq, PostgreSQL, data) so the clean-install acceptance suite
  can run on the same machine. DESTRUCTIVE: disposable VM / CI runner only.
#>
param(
    [Parameter(Mandatory)] [string] $OldBundle,
    [Parameter(Mandatory)] [string] $NewBundle,
    [string] $OutDir = (Join-Path $env:RUNNER_TEMP 'zentriq-upgrade-from-release')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$base = 'https://localhost'
$email = 'admin@upgrade.zentriq.test'
$password = 'Zu-' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(18)).Replace('+', 'x').Replace('/', 'y') + '-5c'
$results = [System.Collections.Generic.List[object]]::new()

function Record($Id, $Name, $Outcome, $Detail) {
    $results.Add([ordered]@{ id = $Id; test = $Name; outcome = $Outcome; detail = $Detail })
    Write-Host ("[{0,-7}] {1} {2} {3}" -f $Outcome, $Id, $Name, $Detail)
}
function Run-Setup([string] $Exe, [string[]] $SetupArgs, [string] $LogName) {
    $log = Join-Path $OutDir "$LogName.log"
    $p = Start-Process -FilePath $Exe -ArgumentList ($SetupArgs + @('/quiet', '/norestart', '/log', "`"$log`"")) -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "$LogName exited with $($p.ExitCode); see $log" }
}
function Wait-Ready {
    $deadline = (Get-Date).AddMinutes(3)
    do { try { $r = Invoke-RestMethod "$base/health/ready"; if ($r.status -eq 'ready') { return $r } } catch { }; Start-Sleep 3 } while ((Get-Date) -lt $deadline)
    throw 'API not ready within 3 minutes'
}
function Login {
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Invoke-WebRequest "$base/api/v1/auth/login" -Method Post -Body (@{ email = $email; password = $password } | ConvertTo-Json) -ContentType 'application/json' -Headers @{ 'X-Zentriq-Request' = '1' } -WebSession $s | Out-Null
    $s
}

$answer = Join-Path $OutDir 'answers.json'
@{ postgres = @{ mode = 'bundled'; port = 5432; dataDir = 'C:\ZentriqUpgradeTest\postgresql' }
   https = @{ port = 443; hostName = 'localhost' }
   admin = @{ organization = 'Upgrade CI'; email = $email; displayName = 'Upgrade Admin'; password = $password } } | ConvertTo-Json -Depth 4 | Set-Content $answer -Encoding UTF8

$ok = $true
try {
    Run-Setup $OldBundle @("ZQ_ANSWERFILE=`"$answer`"") 'old-install'
    $before = (Wait-Ready).checks
    $trialBefore = (Invoke-RestMethod "$base/api/v1/setup/status").trialExpiresAt
    $oldVersion = (Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Console').Version
    Record 'U01' "Install previous release $oldVersion" 'PASS' "schema $($before.schemaVersion)"
} catch { Record 'U01' 'Install previous release' 'FAIL' $_.Exception.Message; $ok = $false }
Remove-Item $answer -Force

if ($ok) {
    try {
        Run-Setup $NewBundle @() 'upgrade'
        $r = Wait-Ready
        if ($r.checks.schemaVersion -ne $r.checks.expectedSchemaVersion) { throw "schema $($r.checks.schemaVersion)/$($r.checks.expectedSchemaVersion)" }
        $s = Login
        $st = Invoke-RestMethod "$base/api/v1/licensing/status" -WebSession $s
        $status = Invoke-RestMethod "$base/api/v1/setup/status"
        if ($status.trialExpiresAt -ne $trialBefore) { throw "trial changed: $trialBefore -> $($status.trialExpiresAt)" }
        if ($st.limits.devices -ne 30) { throw "trial limits after upgrade: $($st.limits | ConvertTo-Json -Compress)" }
        $newVersion = (Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Console').Version
        $arp = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
            Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like 'Zentriq*' -and -not ($_.PSObject.Properties['SystemComponent'] -and $_.SystemComponent -eq 1) }
        $names = @($arp | ForEach-Object DisplayName | Sort-Object -Unique)
        if ($names.Count -ne 1 -or $names[0] -ne 'Zentriq') { throw "Programs and Features shows: $($names -join ', ')" }
        Record 'U02' "Upgrade $oldVersion -> $newVersion keeps data, sign-in and trial; trial moved to 30 devices" 'PASS' "schema $($r.checks.schemaVersion); Programs and Features: '$($names[0])'"
    } catch { Record 'U02' 'Upgrade from the previous release' 'FAIL' $_.Exception.Message; $ok = $false }
} else { Record 'U02' 'Upgrade from the previous release' 'BLOCKED' 'previous release did not install' }

# ---- clean the machine for the clean-install suite -----------------------------------------------
try { Run-Setup $NewBundle @('/uninstall') 'cleanup-uninstall' } catch { Write-Warning $_ }
foreach ($k in Get-ChildItem 'HKLM:\SOFTWARE\PostgreSQL\Installations' -ErrorAction SilentlyContinue) {
    $v = Get-ItemProperty $k.PSPath
    if ($v.Version -like '17.*') {
        Start-Process (Join-Path $v.'Base Directory' 'uninstall-postgresql.exe') -ArgumentList '--mode', 'unattended' -Wait
    }
}
foreach ($d in 'C:\ProgramData\Zentriq', 'C:\ZentriqUpgradeTest', (Join-Path $env:ProgramFiles 'PostgreSQL\17')) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
Remove-Item 'HKLM:\SOFTWARE\Zentriq' -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem Cert:\LocalMachine\Root | Where-Object Subject -like '*Zentriq (self-signed)*' | Remove-Item -ErrorAction SilentlyContinue

$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'upgrade-results.json') -Encoding UTF8
($results | ForEach-Object { "| $($_.id) | $($_.test) | **$($_.outcome)** | $($_.detail -replace '\|','/') |" }) | Set-Content (Join-Path $OutDir 'upgrade-results.md') -Encoding UTF8
if (-not $ok) { exit 1 }
exit 0
