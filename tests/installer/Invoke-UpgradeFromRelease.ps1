#Requires -Version 7.2
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Upgrade test from a previously published release: installs the old bundle silently, then runs the new bundle
  over it and verifies the installation still works (services, schema, sign-in, trial kept and moved to the
  30-device policy). Finally removes everything (Zentriq, PostgreSQL, data) so the clean-install acceptance suite
  can run on the same machine. DESTRUCTIVE: disposable VM / CI runner only.

  With -OldAgentMsi/-NewAgentMsi it also enrolls the old agent before the server upgrade and then installs the new
  agent MSI over it: the agent must be updated in place (one product, same enrollment and certificate, service
  running, new version reported) without an enrollment code.

  -IdPrefix lets CI run it twice: from the last release of an earlier version (U) and from the last published
  build of the same version (S, e.g. 1.1.1.29 -> 1.1.1.31), which must also update in place.
#>
param(
    [Parameter(Mandatory)] [string] $OldBundle,
    [Parameter(Mandatory)] [string] $NewBundle,
    [string] $OutDir = (Join-Path $env:RUNNER_TEMP 'zentriq-upgrade-from-release'),
    [string] $OldAgentMsi,
    [string] $NewAgentMsi,
    [string] $IdPrefix = 'U',
    # Optional: a second old bundle run over the first before the upgrade (reproduces a server where a later build of
    # the same version was installed over an earlier one and only partly applied).
    [string] $MiddleBundle
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

function Api([string] $Method, [string] $Path, $Body, $Session) {
    $p = @{ Uri = "$base/api/v1$Path"; Method = $Method; WebSession = $Session; Headers = @{ 'X-Zentriq-Request' = '1' } }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6); $p.ContentType = 'application/json' }
    Invoke-RestMethod @p
}
function Arp([string] $Like) {
    @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like $Like -and -not ($_.PSObject.Properties['SystemComponent'] -and $_.SystemComponent -eq 1) })
}
$P1 = "${IdPrefix}01"; $P2 = "${IdPrefix}02"; $P3 = "${IdPrefix}03"; $P4 = "${IdPrefix}04"; $P5 = "${IdPrefix}05"; $P6 = "${IdPrefix}06"
$withAgent = [bool]($OldAgentMsi -and $NewAgentMsi)
# msiexec cannot open paths with forward slashes (error 1619), which CI passes in.
if ($withAgent) { $OldAgentMsi = (Resolve-Path $OldAgentMsi).ProviderPath; $NewAgentMsi = (Resolve-Path $NewAgentMsi).ProviderPath }

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
    Record $P1 "Install previous release $oldVersion" 'PASS' "schema $($before.schemaVersion)"
} catch { Record $P1 'Install previous release' 'FAIL' $_.Exception.Message; $ok = $false }

if ($ok -and $MiddleBundle) {
    try {
        Run-Setup $MiddleBundle @() 'middle-install'
        $null = Wait-Ready
        $m = Invoke-WebRequest "$base/api/v1/activity" -SkipHttpErrorCheck -WebSession (Login)
        $entries = @(Arp 'Zentriq')
        $state = if ($m.StatusCode -eq 404) { 'half-updated (new console pages, old server: /activity 404)' } else { "server answers /activity with $($m.StatusCode)" }
        Record $P6 "Install $(Split-Path (Split-Path $MiddleBundle -Parent) -Leaf) over it" 'PASS' "$state; $($entries.Count) 'Zentriq' entr$(if ($entries.Count -eq 1) { 'y' } else { 'ies' }) in Apps and Features"
    } catch { Record $P6 'Install the middle build over it' 'NOT RUN' "could not reproduce the half-updated state silently ($($_.Exception.Message)); continuing from the first build" }
}

$agentOk = $false
$agentFailed = $false
if ($ok -and $withAgent) {
    try {
        $s = Login
        # Builds 63+ protect the agent against removal by default; the test stops the service to run check-ins in the
        # foreground, so switch protection off (older servers do not have the setting).
        try { Api PUT '/agents/settings' @{ checkinMinutes = 30; inventoryMinutes = 180; uninstallProtection = $false } $s | Out-Null } catch { }
        $tok = Api POST '/agents/enrollment-tokens' @{ name = 'upgrade-ci'; validDays = 1; maxUses = 1; serverUrl = 'https://localhost:8444' } $s
        $log = Join-Path $OutDir 'agent-old-install.log'
        $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$OldAgentMsi`"", '/qn', 'SERVERURL=https://localhost:8444', "ENROLLMENTCODE=$($tok.code)", '/l*v', "`"$log`"" -Wait -PassThru   # 20-character codes (build 63+) need the server address
        if ($p.ExitCode -ne 0) { throw "old agent MSI exit $($p.ExitCode)" }
        $deadline = (Get-Date).AddMinutes(4)
        do { Start-Sleep 5; $ag = @(Api GET '/agents' $null $s | ForEach-Object { $_ }) | Where-Object { $_.lastCheckinAt -and $_.status -ne 'revoked' } | Select-Object -First 1 } while (-not $ag -and (Get-Date) -lt $deadline)
        if (-not $ag) { throw 'old agent did not enroll within 4 minutes' }
        $agentBefore = @{ id = $ag.id; version = $ag.version; thumb = (Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent').CertThumbprint; product = (Arp 'Zentriq Agent')[0].PSChildName }
        Record $P3 "Enroll previous agent $($ag.version)" 'PASS' "agent $($ag.id)"
        $agentOk = $true
    } catch { Record $P3 'Enroll previous agent' 'FAIL' $_.Exception.Message; $agentFailed = $true }
}
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
        if ($newVersion -eq $oldVersion) { throw "version unchanged ($newVersion): the new build was not applied" }
        # Every entry, not unique names: a second 'Zentriq' entry means it was installed again instead of updated.
        $entries = @(Arp 'Zentriq' )
        if ($entries.Count -ne 1) { throw "Programs and Features shows $($entries.Count) 'Zentriq' entries: $(($entries | ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion)" }) -join '; ')" }
        $svc = @(Get-Service 'Zentriq*' | Where-Object { $_.Name -ne 'ZentriqAgent' -and $_.StartType -eq 'Automatic' })
        if (@($svc | Where-Object Status -ne 'Running').Count) { throw "services not running: $(($svc | ForEach-Object { "$($_.Name)=$($_.Status)" }) -join ', ')" }
        Record $P2 "Upgrade $oldVersion -> $newVersion in place keeps data, sign-in and trial (30 devices)" 'PASS' "schema $($r.checks.schemaVersion); Programs and Features: one entry '$($entries[0].DisplayName) $($entries[0].DisplayVersion)'"
    } catch { Record $P2 'Upgrade from the previous release' 'FAIL' $_.Exception.Message; $ok = $false }
} else { Record $P2 'Upgrade from the previous release' 'BLOCKED' 'previous release did not install' }

if ($ok) {
    # Features of the new build answer on the upgraded server (a half-applied update leaves these at 404).
    try {
        $s = Login
        $h = @{ 'X-Zentriq-Request' = '1' }
        function Code($Method, $Path, $Body) {
            $p = @{ Uri = "$base/api/v1$Path"; Method = $Method; WebSession = $s; Headers = $h; SkipHttpErrorCheck = $true }
            if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6); $p.ContentType = 'application/json' }
            (Invoke-WebRequest @p).StatusCode
        }
        $checks = [ordered]@{
            'activity log'         = Code GET '/activity' $null
            'software uninstall'   = Code POST '/software/uninstall' @{}
            'agent update'         = Code POST '/agents/update' @{}
            'dashboard overview'   = Code GET '/dashboard/overview' $null
        }
        $bad = @($checks.GetEnumerator() | Where-Object { (($_.Key -in 'activity log', 'dashboard overview') -and $_.Value -ne 200) -or (($_.Key -in 'software uninstall', 'agent update') -and $_.Value -ne 400) })
        if ($bad.Count) { throw "unexpected responses: $(($checks.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')" }
        $ov = Invoke-RestMethod "$base/api/v1/dashboard/overview" -WebSession $s -Headers $h
        if (-not $ov.PSObject.Properties['cores']) { throw 'dashboard overview has no processor (cores) data' }
        # Save a customised dashboard layout with the new chart types (rejected by older servers).
        $dash = @(Invoke-RestMethod "$base/api/v1/dashboards" -WebSession $s -Headers $h | ForEach-Object { $_ }) | Where-Object { $_.owned } | Select-Object -First 1
        $layout = @{ name = 'My dashboard'; shared = $false; filters = @{}; refreshSeconds = 60; widgets = @(
            @{ type = 'kpi.tiles'; title = $null; width = 8; height = 2; settings = @{} }, @{ type = 'hw.cores'; title = $null; width = 4; height = 2; settings = @{} },
            @{ type = 'hw.disk'; title = $null; width = 4; height = 2; settings = @{} }, @{ type = 'assets.seen'; title = $null; width = 4; height = 2; settings = @{} }) }
        $code = if ($dash) { Code PUT "/dashboards/$($dash.id)" $layout } else { Code POST '/dashboards' $layout }
        if ($code -notin 200, 201) { throw "saving a dashboard layout returned $code" }
        $saved = @(Invoke-RestMethod "$base/api/v1/dashboards" -WebSession $s -Headers $h | ForEach-Object { $_ }) | Where-Object { $_.owned } | Select-Object -First 1
        if ((@($saved.widgets) | ForEach-Object type) -join ',' -ne 'kpi.tiles,hw.cores,hw.disk,assets.seen') { throw "saved layout: $((@($saved.widgets) | ForEach-Object type) -join ',')" }
        Record $P5 'New features work after the update' 'PASS' "$(($checks.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', '); dashboard layout saved and read back"
    } catch { Record $P5 'New features work after the update' 'FAIL' $_.Exception.Message; $ok = $false }
}

if ($withAgent) {
    if ($ok -and $agentOk) {
        try {
            # The previous agent must keep working against the upgraded server (servers and agents are updated separately).
            $exe = Join-Path $env:ProgramFiles 'Zentriq Agent\ZentriqAgent.exe'
            $s = Login
            $t0 = (@(Api GET '/agents' $null $s | ForEach-Object { $_ }) | Where-Object { $_.id -eq $agentBefore.id }).lastCheckinAt
            Start-Sleep 2
            Stop-Service ZentriqAgent; & $exe --once *> $null; Start-Service ZentriqAgent
            $t1 = (@(Api GET '/agents' $null $s | ForEach-Object { $_ }) | Where-Object { $_.id -eq $agentBefore.id }).lastCheckinAt
            if (-not $t1 -or $t1 -eq $t0) { throw "previous agent did not check in to the upgraded server ($t0 -> $t1)" }
            $log = Join-Path $OutDir 'agent-upgrade.log'
            $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$NewAgentMsi`"", '/qn', '/l*v', "`"$log`"" -Wait -PassThru
            if ($p.ExitCode -notin 0, 3010) { throw "new agent MSI exit $($p.ExitCode); see $log" }
            $entries = @(Arp 'Zentriq Agent')
            if ($entries.Count -ne 1) { throw "Programs and Features shows $($entries.Count) 'Zentriq Agent' entries" }
            if ($entries[0].PSChildName -eq $agentBefore.product) { throw 'agent product code unchanged: the new MSI did not replace the old one' }
            $reg = Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent'
            if ($reg.CertThumbprint -ne $agentBefore.thumb) { throw 'agent certificate/enrollment was not kept' }
            if ((Get-Service ZentriqAgent).Status -ne 'Running') { throw 'ZentriqAgent not running after upgrade' }
            $deadline = (Get-Date).AddMinutes(3)
            do {
                Stop-Service ZentriqAgent; & $exe --once *> $null; Start-Service ZentriqAgent
                $list = @(Api GET '/agents' $null $s | ForEach-Object { $_ } | Where-Object { $_.status -ne 'revoked' })
                $ag = $list | Where-Object { $_.id -eq $agentBefore.id }
                if ($ag -and $ag.version -ne $agentBefore.version) { break }
                Start-Sleep 10
            } while ((Get-Date) -lt $deadline)
            if ($list.Count -ne 1) { throw "$($list.Count) agent registrations (expected the same single one)" }
            if (-not $ag -or $ag.version -eq $agentBefore.version) { throw "agent still reports $($agentBefore.version)" }
            if (-not $ag.canSelfUpdate) { throw 'the updated agent does not report the self-update capability' }
            Record $P4 "Agent $($agentBefore.version) -> $($ag.version) updated in place (no enrollment code)" 'PASS' "old agent checked in to the upgraded server; same registration $($ag.id) and certificate; console updates now available; one 'Zentriq Agent' entry ($($entries[0].DisplayVersion)); service running"
        } catch { Record $P4 'Agent updated in place' 'FAIL' $_.Exception.Message; $ok = $false }
    } else { Record $P4 'Agent updated in place' 'BLOCKED' 'server upgrade or old agent failed' }
    $a = Arp 'Zentriq Agent' | Select-Object -First 1
    if ($a) { Start-Process msiexec.exe -ArgumentList '/x', $a.PSChildName, '/qn' -Wait | Out-Null }
}

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
if (-not $ok -or $agentFailed) { exit 1 }
exit 0
