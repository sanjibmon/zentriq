#Requires -Version 7.2
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  End-to-end acceptance test of Zentriq-Setup-x64.exe on a clean Windows Server machine.

.DESCRIPTION
  Installs silently with an answer file, then verifies the installation the way a customer would use it:
  services, PostgreSQL, migrations, HTTPS, administrator sign-in, trial, repair, restart, backup, license
  rejection, company profile, Zentriq Agent (download, enrollment, inventory, mTLS port, revocation, uninstall),
  agentless WinRM inventory with a stored credential (and merge with the agent record), credential-free logs,
  upgrade, uninstall (data preserved) and reinstall (trial not reset).

  Every check is recorded as PASS / FAIL / BLOCKED / NOT RUN in <OutDir>\installer-test-results.json and .md.
  DESTRUCTIVE: only run on a disposable VM or CI runner.

.EXAMPLE
  ./Invoke-InstallerAcceptance.ps1 -Bundle .\Zentriq-Setup-x64.exe -UpgradeBundle .\v2\Zentriq-Setup-x64.exe -OutDir C:\zq-test
#>
param(
    [Parameter(Mandatory)] [string] $Bundle,
    [string] $UpgradeBundle,
    [string] $OutDir = (Join-Path $env:RUNNER_TEMP 'zentriq-acceptance'),
    [int] $HttpsPort = 443
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'
trap { Write-Host "ERROR: $($_ | Out-String)"; Write-Host $_.ScriptStackTrace; break }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
$base = "https://localhost$(if ($HttpsPort -ne 443) { ":$HttpsPort" })"
$dataDir = Join-Path $env:ProgramData 'Zentriq'
$ctl = Join-Path $env:ProgramFiles 'Zentriq\tools\zentriqctl.exe'
$adminEmail = 'admin@ci.zentriq.test'
$adminPassword = 'Zq-' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(18)).Replace('+', 'x').Replace('/', 'y') + '-9a'

function Record([string] $Id, [string] $Name, [string] $Outcome, [string] $Detail = '') {
    $results.Add([ordered]@{ id = $Id; test = $Name; outcome = $Outcome; detail = $Detail; atUtc = (Get-Date).ToUniversalTime().ToString('o') })
    $color = @{ PASS = 'Green'; FAIL = 'Red'; BLOCKED = 'Yellow'; 'NOT RUN' = 'DarkGray' }[$Outcome]
    Write-Host ("[{0,-7}] {1} {2} {3}" -f $Outcome, $Id, $Name, $Detail) -ForegroundColor $color
}
function Check([string] $Id, [string] $Name, [scriptblock] $Body) {
    try {
        $detail = & $Body
        Record $Id $Name 'PASS' ([string]($detail | Select-Object -Last 1))
        return $true
    } catch {
        # A test that cannot run for an outside reason (e.g. no internet on the runner) throws "BLOCKED: …".
        if ($_.Exception.Message -like 'BLOCKED:*') { Record $Id $Name 'BLOCKED' $_.Exception.Message.Substring(8).Trim(); return $false }
        Record $Id $Name 'FAIL' $_.Exception.Message
        return $false
    }
}
function Assert($Condition, [string] $Message) { if (-not $Condition) { throw $Message } }

function Run-Setup([string] $Exe, [string[]] $SetupArgs, [string] $LogName) {
    $log = Join-Path $OutDir "$LogName.log"
    $p = Start-Process -FilePath $Exe -ArgumentList ($SetupArgs + @('/quiet', '/norestart', '/log', "`"$log`"")) -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "$LogName exited with $($p.ExitCode); see $log" }
    return "exit $($p.ExitCode)"
}
function Login {
    # Sign-in is rate limited (10 per minute per address); the suite signs in often, so wait out a 429.
    $body = @{ email = $adminEmail; password = $adminPassword } | ConvertTo-Json
    for ($i = 0; ; $i++) {
        $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $r = Invoke-WebRequest "$base/api/v1/auth/login" -Method Post -Body $body -ContentType 'application/json' -Headers @{ 'X-Zentriq-Request' = '1' } -WebSession $s -SkipHttpErrorCheck
        if ($r.StatusCode -eq 429 -and $i -lt 5) { Start-Sleep 15; continue }
        Assert ($r.StatusCode -eq 200) "login returned $($r.StatusCode)"
        return $s
    }
}
# PowerShell returns non-text media types (e.g. application/problem+json) as byte[]; read them as UTF-8.
function Body($Response) { if ($Response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($Response.Content) } else { [string]$Response.Content } }
function Setup-Status { Invoke-RestMethod "$base/api/v1/setup/status" }
function Wait-Ready([int] $Seconds = 180) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        try { $r = Invoke-WebRequest "$base/health/ready" -SkipHttpErrorCheck; if ($r.StatusCode -eq 200) { return $r } } catch { }
        Start-Sleep 3
    } while ((Get-Date) -lt $deadline)
    throw "API not ready within $Seconds s"
}

# ---- environment ------------------------------------------------------------------------------
$os = Get-CimInstance Win32_OperatingSystem
Write-Host "Environment: $($os.Caption) $($os.Version), $([math]::Round($os.TotalVisibleMemorySize/1MB,1)) GB RAM, $env:COMPUTERNAME"
$existingPg = Get-ChildItem 'HKLM:\SOFTWARE\PostgreSQL\Installations' -ErrorAction SilentlyContinue
foreach ($k in $existingPg) {
    $v = Get-ItemProperty $k.PSPath
    Write-Host "Pre-existing PostgreSQL on this machine: $($k.PSChildName) $($v.Version) at $($v.'Base Directory')"
    if ($v.Version -like '17.*') {
        # A pre-installed PostgreSQL 17 (CI image) would collide with the bundled one; remove it from this disposable VM.
        $un = Join-Path $v.'Base Directory' 'uninstall-postgresql.exe'
        Write-Host "Removing pre-installed PostgreSQL 17 ($un) from this disposable machine."
        Start-Process $un -ArgumentList '--mode', 'unattended' -Wait
        $dd = $v.PSObject.Properties['Data Directory']
        if ($dd) { Remove-Item $dd.Value -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ---- answer file (silent install) -------------------------------------------------------------
$answer = Join-Path $OutDir 'answers.json'
@{
    postgres = @{ mode = 'bundled'; port = 5432; dataDir = 'C:\ZentriqTestData\postgresql' }
    https    = @{ port = $HttpsPort; hostName = 'localhost' }
    admin    = @{ organization = 'Zentriq CI'; email = $adminEmail; displayName = 'CI Administrator'; password = $adminPassword }
} | ConvertTo-Json -Depth 4 | Set-Content $answer -Encoding UTF8

# A previous failed attempt can leave a PostgreSQL data folder behind; setup must recover (T01 + T32).
New-Item -ItemType Directory -Force -Path 'C:\ZentriqTestData\postgresql' | Out-Null
Set-Content 'C:\ZentriqTestData\postgresql\PG_VERSION' '17' -Encoding ascii

# ---- T01 install -------------------------------------------------------------------------------
$installed = Check 'T01' 'First-time installation (silent, answer file)' { $sw = [Diagnostics.Stopwatch]::StartNew(); $r = Run-Setup $Bundle @("ZQ_ANSWERFILE=`"$answer`"", 'ZQ_SECTIONS=all') 'install'; "$r in $([int]$sw.Elapsed.TotalMinutes) min" }
Remove-Item $answer -Force   # contains the administrator password

if (-not $installed) {
    foreach ($t in 'T02 PostgreSQL provisioning', 'T03 Database migration', 'T04 Administrator sign-in', 'T05 API and worker services', 'T06 Trial initialization',
                   'T07 HTTPS', 'T08 zentriqctl verify', 'T09 Restart services', 'T10 Repair', 'T11 Backup', 'T12 Tampered license', 'T13 Upgrade', 'T14 Logs free of credentials', 'T15 Uninstall', 'T16 Reinstall', 'T22 Shortcuts', 'T23 Web console', 'T24 Trial policy', 'T25 Company profile', 'T26 Agent install', 'T27 Agent port', 'T28 Agentless WinRM', 'T29 Agent revoke/uninstall', 'T32 Leftover data folder', 'T33 Show enrollment code', 'T34 Delete scans', 'T35 MFA', 'T36 Password reset', 'T37 AD connection', 'T41 Asset type and IPv4', 'T42 Collect all', 'T43 Service via agent', 'T44 Service agentless', 'T45 Software uninstall', 'T46 Drill-down and offline', 'T47 Agent update from the console', 'T48 Reports', 'T49 Vulnerability management', 'T50 Windows vulnerabilities and workflow', 'T51 Live Microsoft data', 'T52 Report analytics and PDF', 'T53 Security configuration', 'T54 Integrations', 'T55 API tokens', 'T56 Departments', 'T57 Change history', 'T58 Company logo', 'T59 Remote support', 'T60 Remote support via agent', 'T61 Certificate follows the server', 'T62 Devices without an agent', 'T63 Network inventory', 'T64 Non-IT assets', 'T65 Remote quick support', 'T66 Non-IT groups and departments', 'T67 Agent uninstall protection', 'T68 Live agent connection', 'T69 Locations and IT accessories', 'T70 Manual hardware details', 'T71 Standard and Advanced installation', 'T72 Asset lifecycle', 'T73 Cloud and infrastructure', 'T74 Own asset tags, people and bulk upload', 'T75 Backup and restore', 'T76 Roles and groups', 'T77 Log retention and export', 'T78 Agent stop and disable protection', 'T79 Automatic agent update', 'T80 Agent check-in and diagnosis commands', 'T81 Licensing plans and online activation', 'T82 Software Deployment policy', 'T83 Application control on Windows', 'T84 Push deployment', 'T85 Remote support agent tamper protection', 'T86 Software licenses', 'T87 Web apps and accounts', 'T88 Network connected devices', 'T89 Warranty lookup', 'T90 Session recording', 'T91 Directory sign-in', 'T92 Patch management', 'T93 Scripts and remediation', 'T94 Tray icon and self-service', 'T95 Software metering and device experience', 'T96 Endpoint privilege management', 'T97 BitLocker management', 'T98 USB device control') {
        Record $t.Substring(0, 3) $t.Substring(4) 'BLOCKED' 'installation failed'
    }
} else {
    Check 'T02' 'PostgreSQL provisioned as a Windows service' {
        $svc = Get-Service 'postgresql-x64-17'; Assert ($svc.Status -eq 'Running') "status $($svc.Status)"
        $listen = Get-NetTCPConnection -LocalPort 5432 -State Listen; Assert (-not ($listen | Where-Object { $_.LocalAddress -notin '127.0.0.1', '::1' })) 'PostgreSQL listens on a non-loopback address'
        'running, loopback-only'
    } | Out-Null
    Check 'T32' 'Leftover data folder from an earlier failed attempt is set aside, not fatal' {
        $aside = @(Get-ChildItem 'C:\ZentriqTestData' -Directory -Filter 'postgresql.incomplete-*')
        Assert ($aside.Count -eq 1) 'leftover data folder was not set aside'
        Assert (Test-Path 'C:\ZentriqTestData\postgresql\PG_VERSION') 'new data folder missing'
        "moved to $($aside[0].Name)"
    } | Out-Null
    Check 'T05' 'API and worker services running under virtual accounts' {
        foreach ($n in 'ZentriqApi', 'ZentriqWorker') {
            $s = Get-CimInstance Win32_Service -Filter "Name='$n'"
            Assert ($s.State -eq 'Running') "$n is $($s.State)"
            Assert ($s.StartName -eq "NT SERVICE\$n") "$n runs as $($s.StartName)"
        }
        $r = Wait-Ready; "ready: $($r.Content)"
    } | Out-Null
    Check 'T03' 'Database migrations applied' {
        $r = Invoke-RestMethod "$base/health/ready"
        Assert ($r.checks.schemaVersion -eq $r.checks.expectedSchemaVersion) "schema $($r.checks.schemaVersion)/$($r.checks.expectedSchemaVersion)"
        "schema version $($r.checks.schemaVersion)"
    } | Out-Null
    $script:trialEnd = $null
    Check 'T06' 'Trial initialized for 14 days' {
        $s = Setup-Status
        Assert ($s.initialized) 'not initialized'
        Assert ($s.licenseMode -eq 'Trial') "mode $($s.licenseMode)"
        $days = ([DateTimeOffset]$s.trialExpiresAt - [DateTimeOffset]::UtcNow).TotalDays
        Assert ($days -gt 13.9 -and $days -le 14.01) "trial ends in $days days"
        $script:trialEnd = $s.trialExpiresAt
        "expires $($s.trialExpiresAt)"
    } | Out-Null
    Check 'T04' 'First administrator can sign in' {
        $s = Login
        $me = Invoke-RestMethod "$base/api/v1/auth/me" -WebSession $s
        "signed in as $($me.email)"
    } | Out-Null
    Check 'T07' 'HTTPS certificate is valid and trusted on the server' {
        $r = Invoke-WebRequest "$base/" # no -SkipCertificateCheck: chain and name must validate
        Assert ($r.StatusCode -eq 200) "status $($r.StatusCode)"
        Assert ($r.Content -match 'Zentriq') 'console page does not mention Zentriq'
        $fw = Get-NetFirewallRule -DisplayName 'Zentriq Console (HTTPS)' -ErrorAction SilentlyContinue
        Assert $fw 'firewall rule missing'
        'TLS OK, firewall rule present'
    } | Out-Null
    Check 'T22' 'Start menu and Desktop shortcuts use the Zentriq icon' {
        $iconPath = Join-Path $env:ProgramFiles 'Zentriq\zentriq.ico'
        Assert (Test-Path $iconPath) 'zentriq.ico not installed'
        $start = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Zentriq\Zentriq Web Console.url'
        $desk = Join-Path $env:PUBLIC 'Desktop\Zentriq Web Console.url'
        foreach ($f in $start, $desk) {
            Assert (Test-Path $f) "missing $f"
            $c = Get-Content $f -Raw
            Assert ($c -match '(?im)^IconFile=.*zentriq\.ico') "$f has no Zentriq icon: $c"
            Assert ($c -match '(?im)^URL=https://') "$f has no https URL"
        }
        'Start menu + Desktop shortcuts with zentriq.ico'
    } | Out-Null
    Check 'T23' 'Web console sign-in page is served (not the placeholder)' {
        $r = Invoke-WebRequest "$base/" -SkipCertificateCheck
        Assert ($r.Content -notmatch 'added to this folder by the frontend build') 'placeholder page is still installed'
        $js = [regex]::Match($r.Content, 'src="(/assets/[^"]+\.js)"').Groups[1].Value
        Assert $js 'no application script referenced by index.html'
        $bundle = Invoke-WebRequest "$base$js" -SkipCertificateCheck
        Assert ($bundle.Content -match 'Sign in') 'application bundle has no sign-in page'
        # After an upgrade the browser must load the new console: the start page is never cached, hashed assets are.
        $deep = Invoke-WebRequest "$base/agents" -SkipCertificateCheck
        foreach ($page in @($r, $deep)) { Assert ("$($page.Headers['Cache-Control'])" -match 'no-cache' -and "$($page.Headers['Cache-Control'])" -match 'no-store') "start page may be cached: $($page.Headers['Cache-Control'])" }
        Assert ("$($bundle.Headers['Cache-Control'])" -match 'immutable') "assets not cacheable: $($bundle.Headers['Cache-Control'])"
        "index + $js ($([int]($bundle.RawContentLength/1KB)) KB); start page not cached, assets immutable"
    } | Out-Null
    Check 'T08' 'zentriqctl verify (end-to-end health)' { & $ctl verify | Out-Host; Assert ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"; 'all checks passed' } | Out-Null

    Check 'T09' 'Service restart does not reset the trial' {
        Restart-Service ZentriqWorker, ZentriqApi -Force
        Wait-Ready | Out-Null
        Assert ((Setup-Status).trialExpiresAt -eq $script:trialEnd) 'trial changed'
        'trial unchanged'
    } | Out-Null
    Check 'T10' 'Repair keeps data and trial' {
        Run-Setup $Bundle @('/repair') 'repair' | Out-Null
        Wait-Ready | Out-Null
        Assert ((Setup-Status).trialExpiresAt -eq $script:trialEnd) 'trial changed after repair'
        Login | Out-Null
        'repaired; trial unchanged; sign-in works'
    } | Out-Null
    Check 'T11' 'Encrypted backup and deep restore validation' {
        & $ctl backup --label ci | Out-Host; Assert ($LASTEXITCODE -eq 0) "backup exit $LASTEXITCODE"
        $f = Get-ChildItem (Join-Path $dataDir 'Backups') -Filter '*.zbak' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        Assert $f 'no backup file'
        & $ctl backup-verify $f.FullName --deep | Out-Host; Assert ($LASTEXITCODE -eq 0) "verify exit $LASTEXITCODE"
        "$($f.Name) ($([int]($f.Length/1KB)) KB)"
    } | Out-Null
    Check 'T12' 'Tampered/unsigned license is rejected' {
        $s = Login
        $fake = @{ format = 'zentriq-license'; keyId = 'zentriq-dev-2026-10'; algorithm = 'ES256'; payload = 'eyJ4IjoxfQ=='; signature = 'AAAA' } | ConvertTo-Json -Compress
        $r = Invoke-WebRequest "$base/api/v1/licensing/import" -Method Post -Body (@{ document = $fake } | ConvertTo-Json) -ContentType 'application/json' -Headers @{ 'X-Zentriq-Request' = '1' } -WebSession $s -SkipHttpErrorCheck
        Assert ($r.StatusCode -ge 400 -and $r.StatusCode -lt 500) "import returned $($r.StatusCode)"
        Assert ((Setup-Status).licenseMode -eq 'Trial') 'license state changed'
        "rejected with $($r.StatusCode)"
    } | Out-Null

    # ---- 1.1: trial policy, company profile, agent, agentless (WinRM) -----------------------------
    function Api([string] $Method, [string] $Path, $Body = $null, $Session) {
        $p = @{ Uri = "$base/api/v1$Path"; Method = $Method; WebSession = $Session; Headers = @{ 'X-Zentriq-Request' = '1' }; ContentType = 'application/json' }
        if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6) }
        Invoke-RestMethod @p
    }
    Check 'T24' 'Trial: every module enabled, 30 devices in total' {
        $s = Login
        $st = Api GET '/licensing/status' -Session $s
        Assert ($st.modules -contains '*') "modules: $($st.modules -join ',')"
        Assert ($st.limits.devices -eq 30) "device limit $($st.limits.devices)"
        Assert (@($st.usage | Where-Object workload -eq 'devices').Count -eq 1) 'no total devices usage row'
        "limits: $($st.limits | ConvertTo-Json -Compress)"
    } | Out-Null
    Check 'T81' 'Licensing plans: trial 30/50/10/1, free 25/25/5/0, four usage metrics, online activation errors are clear' {
        $s = Login
        $st = Api GET '/licensing/status' -Session $s
        Assert ($st.plans.trial.devices -eq 30 -and $st.plans.trial.nonit_assets -eq 50 -and $st.plans.trial.cloud_instances -eq 10 -and $st.plans.trial.technicians -eq 1) "trial plan: $($st.plans.trial | ConvertTo-Json -Compress)"
        Assert ($st.plans.free.devices -eq 25 -and $st.plans.free.nonit_assets -eq 25 -and $st.plans.free.cloud_instances -eq 5 -and $st.plans.free.technicians -eq 0) "free plan: $($st.plans.free | ConvertTo-Json -Compress)"
        Assert ($st.limits.nonit_assets -eq 50 -and $st.limits.cloud_instances -eq 10 -and $st.limits.technicians -eq 1) "trial limits: $($st.limits | ConvertTo-Json -Compress)"
        $metrics = @($st.usage | Where-Object { $_.metric }) | ForEach-Object workload
        foreach ($m in 'devices', 'nonit_assets', 'cloud_instances', 'technicians') { Assert ($metrics -contains $m) "usage metric $m missing: $($metrics -join ',')" }
        Assert ($st.online.serverUrl -eq 'https://license.zentriq.com' -and -not $st.online.active) "online: $($st.online | ConvertTo-Json -Compress)"
        function Raw81([string] $Method, [string] $Path, $Body) {
            $r = Invoke-WebRequest "$base/api/v1$Path" -Method $Method -Body ($Body | ConvertTo-Json) -ContentType 'application/json' -Headers @{ 'X-Zentriq-Request' = '1' } -WebSession $s -SkipHttpErrorCheck
            $text = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
            [pscustomobject]@{ StatusCode = [int]$r.StatusCode; Content = $text }
        }
        $bad = Raw81 POST '/licensing/activate' @{ licenseKey = 'not-a-key' }
        Assert ($bad.StatusCode -eq 400 -and $bad.Content -match 'ZQL-') "bad key: $($bad.StatusCode) $($bad.Content)"
        $http = Raw81 PUT '/licensing/server' @{ url = 'http://license.example.test' }
        Assert ($http.StatusCode -eq 400) "plain http server address accepted: $($http.StatusCode)"
        Api PUT '/licensing/server' @{ url = 'https://127.0.0.1:9' } -Session $s | Out-Null
        $down = Raw81 POST '/licensing/activate' @{ licenseKey = 'ZQL-00000-11111-22222-33333' }
        Assert ($down.StatusCode -eq 502 -and $down.Content -match 'could not be reached') "unreachable license server: $($down.StatusCode) $($down.Content)"
        Api PUT '/licensing/server' @{ url = '' } -Session $s | Out-Null
        Assert ((Api GET '/licensing/status' -Session $s).online.serverUrl -eq 'https://license.zentriq.com') 'server address not reset'
        $mode = (Api GET '/licensing/status' -Session $s).mode
        Assert ($mode -eq 'Trial') "mode after failed activation: $mode"
        "plans and metrics reported; bad key 400, http address 400, unreachable server 502 with a clear message; still Trial"
    } | Out-Null
    Check 'T25' 'Company profile can be edited and read back' {
        $s = Login
        $body = @{ name = 'Zentriq CI Ltd'; legalName = 'Zentriq CI Private Limited'; city = 'Bengaluru'; country = 'India'; contactName = 'CI Admin'; contactEmail = 'it@ci.zentriq.test'; contactPhone = '+91 80 0000 0000' }
        Api PUT '/organization' $body -Session $s | Out-Null
        $o = Api GET '/organization' -Session $s
        Assert ($o.profile.name -eq 'Zentriq CI Ltd' -and $o.profile.contactEmail -eq 'it@ci.zentriq.test') "profile: $($o.profile | ConvertTo-Json -Compress)"
        "profile saved ($($o.profile.legalName))"
    } | Out-Null

    $script:agentAsset = $null
    $agentMsi = Join-Path $OutDir 'ZentriqAgent-x64.msi'
    # An agent left from an earlier suite on this machine would keep its old enrollment: the clean-install suite needs none.
    if (Get-Service ZentriqAgent -ErrorAction SilentlyContinue) { Write-Warning 'A Zentriq Agent is already installed on this machine before T26.' }
    $agentInstalled = Check 'T26' 'Agent: enrollment code, MSI download from the console, silent install, inventory received' {
        $s = Login
        $pkg = Api GET '/agents/package' -Session $s
        Assert ($pkg.enrollmentAvailable) 'agent CA not provisioned'
        Assert ($pkg.package) 'agent MSI is not served by the console'
        Invoke-WebRequest "$base$($pkg.package.url)" -OutFile $agentMsi
        $sig = Get-AuthenticodeSignature $agentMsi
        # Uninstall protection is tested on its own (T67); the service must be stoppable for the foreground check-ins below.
        Api PUT '/agents/settings' @{ checkinMinutes = 30; inventoryMinutes = 180; uninstallProtection = $false } -Session $s | Out-Null
        $tok = Api POST '/agents/enrollment-tokens' @{ name = 'ci'; validDays = 1; maxUses = 2; serverUrl = 'https://localhost:8444' } -Session $s
        Assert ($tok.code -match '^[0-9A-Z]{5}(-[0-9A-Z]{5}){3}$') "enrollment code is not 20 characters: $($tok.code)"
        Assert ($tok.installCommand -match 'SERVERURL=' -and $tok.installCommand -match 'CHECKININTERVAL=30') "install command: $($tok.installCommand)"
        $log = Join-Path $OutDir 'agent-install.log'
        $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$agentMsi`"", '/qn', "SERVERURL=https://localhost:8444", "ENROLLMENTCODE=$($tok.code)", 'CHECKININTERVAL=2', 'INVENTORYINTERVAL=30', '/l*v', "`"$log`"" -Wait -PassThru
        Assert ($p.ExitCode -eq 0) "agent MSI exit $($p.ExitCode); see $log"
        Assert ((Get-Service ZentriqAgent).Status -eq 'Running') 'ZentriqAgent service not running'
        Assert (-not (Select-String -Path $log -SimpleMatch $tok.code -Quiet)) 'enrollment code written to the MSI log'
        Assert (-not (Select-String -Path $log -SimpleMatch ($tok.code -replace '-', '') -Quiet)) 'enrollment code written to the MSI log'
        $deadline = (Get-Date).AddMinutes(4)
        do {
            Start-Sleep 5
            $a = @(Api GET '/agents' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.assetId -and $_.lastInventoryAt } | Select-Object -First 1
        } while (-not $a -and (Get-Date) -lt $deadline)
        if (-not $a) { & (Join-Path $env:ProgramFiles 'Zentriq Agent\ZentriqAgent.exe') --status | Out-Host; throw 'agent did not enroll and send inventory within 4 minutes' }
        $script:agentAsset = $a.assetId
        $script:agentId = $a.id
        $inv = Api GET "/assets/$($a.assetId)/inventory" -Session $s
        Assert ($inv.source -eq 'agent') "inventory source $($inv.source)"
        Assert (@($inv.software).Count -gt 5) "only $(@($inv.software).Count) software items"
        Assert ($inv.document.hardware.memoryBytes -gt 0) 'no memory size'
        Assert ($inv.document.device.serialNumber -or $inv.document.device.smbiosUuid) 'no serial number or SMBIOS UUID'
        $reg = Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent'
        Assert (-not $reg.PSObject.Properties['EnrollmentCode']) 'enrollment code left in the registry'
        $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Thumbprint -eq $reg.CertThumbprint
        Assert $cert 'agent certificate not in LocalMachine\My'
        Assert ($a.checkinMinutes -eq 2 -and $a.inventoryMinutes -eq 30) "intervals chosen at installation not used: check-in $($a.checkinMinutes), inventory $($a.inventoryMinutes)"
        Assert ($a.health -eq 'online') "agent health $($a.health)"
        "$($a.hostname): 20-character code; check-in every $($a.checkinMinutes) min, inventory every $($a.inventoryMinutes) min (chosen at installation); $(@($inv.software).Count) applications, $([math]::Round($inv.document.hardware.memoryBytes/1GB)) GB RAM; MSI signature: $($sig.Status)"
    }
    if ($agentInstalled) {
        Check 'T80' 'Agent commands on the device: --checkin-now contacts the server at once, --diagnose finds no problem, --restart' {
            $s = Login
            $exe = Join-Path $env:ProgramFiles 'Zentriq Agent\ZentriqAgent.exe'
            $before = (@(Api GET '/agents' -Session $s) | Where-Object { $_.id -eq $script:agentId } | Select-Object -First 1).lastCheckinAt
            Start-Sleep 2
            $out = & $exe --checkin-now 2>&1 | Out-String
            Assert ($LASTEXITCODE -eq 0 -and $out -match 'Checked in') "--checkin-now exit $LASTEXITCODE`: $out"
            $after = (@(Api GET '/agents' -Session $s) | Where-Object { $_.id -eq $script:agentId } | Select-Object -First 1).lastCheckinAt
            Assert ([datetime]$after -gt [datetime]$before) "last check-in not updated ($before -> $after)"
            $diag = & $exe --diagnose 2>&1 | Out-String
            Set-Content (Join-Path $OutDir 'agent-diagnose.txt') $diag
            Assert ($LASTEXITCODE -eq 0 -and $diag -match 'No problem found' -and $diag -match 'server key matches') "--diagnose exit $LASTEXITCODE (see agent-diagnose.txt)"
            & $exe --restart | Out-Host
            Assert ($LASTEXITCODE -eq 0) "--restart exit $LASTEXITCODE"
            $deadline = (Get-Date).AddSeconds(90)
            do { Start-Sleep 3; $st = (Get-Service ZentriqAgent).Status } while ($st -ne 'Running' -and (Get-Date) -lt $deadline)
            Start-Sleep 10
            Assert ((Get-Service ZentriqAgent).Status -eq 'Running') 'agent not running after --restart'
            "--checkin-now: $(($out -split "`n" | Where-Object { $_ -match 'Checked in' } | Select-Object -First 1).Trim()); --diagnose: no problem; --restart: running again"
        } | Out-Null

        Check 'T68' 'Live agent connection: online within seconds, offline the moment the agent stops, queued work picked up at once; installer interval lists in order' {
            $s = Login
            function AgentNow { @(Api GET '/agents' -Session $s) | Where-Object { $_.id -eq $script:agentId } | Select-Object -First 1 }
            $deadline = (Get-Date).AddSeconds(90)
            do { Start-Sleep 2; $a = AgentNow } while (-not ($a.connected -and $a.online) -and (Get-Date) -lt $deadline)
            Assert ($a.live -and $a.connected -and $a.online) "no live connection (live=$($a.live), connected=$($a.connected), online=$($a.online), health=$($a.health))"
            # Stopping the agent closes its connection: offline at once (not after two check-in intervals).
            $t0 = Get-Date
            Stop-Service ZentriqAgent
            do { Start-Sleep 1; $a = AgentNow } while ($a.online -and ((Get-Date) - $t0).TotalSeconds -lt 30)
            $offAfter = [int]((Get-Date) - $t0).TotalSeconds
            Assert (-not $a.online) 'still online 30 seconds after the agent service stopped'
            Assert ($a.health -ne 'online') "health $($a.health) after the agent stopped"
            $t1 = Get-Date
            Start-Service ZentriqAgent
            do { Start-Sleep 1; $a = AgentNow } while (-not $a.online -and ((Get-Date) - $t1).TotalSeconds -lt 60)
            $onAfter = [int]((Get-Date) - $t1).TotalSeconds
            Assert ($a.online) 'not online 60 seconds after the agent service started'
            Assert ($a.lastOutage -and ([datetime]$a.lastOutage.from) -lt ([datetime]$a.lastOutage.to)) "the gap was not recorded: $($a.lastOutage | ConvertTo-Json -Compress)"
            # "Collect now" wakes the agent through its live connection (no waiting for the 2-minute check-in).
            Start-Sleep 20
            $before = [datetime](AgentNow).lastInventoryAt
            $t2 = Get-Date
            Api POST "/agents/$($script:agentId)/collect" @{} -Session $s | Out-Null
            do { Start-Sleep 2; $a = AgentNow } while (([datetime]$a.lastInventoryAt) -le $before -and ((Get-Date) - $t2).TotalSeconds -lt 100)
            $collectAfter = [int]((Get-Date) - $t2).TotalSeconds
            Assert (([datetime]$a.lastInventoryAt) -gt $before) 'collect now was not picked up within 100 seconds'
            # The installer's interval lists keep their order (5 min, 15 min, 30 min, 1 hour, …), not alphabetical.
            function MsiRows([string] $Path, [string] $Sql, [int] $Columns) {
                $wi = New-Object -ComObject WindowsInstaller.Installer
                $db = $wi.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $wi, [object[]]@([System.IO.Path]::GetFullPath($Path), [int]0))
                $v = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, [object[]]@([string]$Sql))
                $null = $v.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $v, $null)
                $rows = @()
                while ($true) {
                    $r = $v.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $v, $null)
                    if ($null -eq $r) { break }
                    $rows += ,@(1..$Columns | ForEach-Object { [string]$r.GetType().InvokeMember('StringData', 'GetProperty', $null, $r, [object[]]@([int]$_)) })
                }
                $null = $v.GetType().InvokeMember('Close', 'InvokeMethod', $null, $v, $null)
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($db) | Out-Null
                return ,$rows
            }
            $sorted = @{}
            foreach ($row in (MsiRows ([string]$agentMsi) "SELECT ``Control``, ``Attributes`` FROM ``Control`` WHERE ``Dialog_`` = 'ZqEnrollDlg'" 2)) {
                if ($row[0] -in 'Checkin', 'Inventory') { $sorted[$row[0]] = (([int]$row[1] -band 0x10000) -ne 0) }
            }
            Assert ($sorted['Checkin'] -and $sorted['Inventory']) "installer interval lists are shown alphabetically: $($sorted | ConvertTo-Json -Compress)"
            $combo = MsiRows ([string]$agentMsi) "SELECT ``Value``, ``Order`` FROM ``ComboBox`` WHERE ``Property`` = 'CHECKININTERVAL'" 2
            $vals = @($combo | Sort-Object { [int]$_[1] } | ForEach-Object { $_[0] })
            Assert (($vals -join ',') -eq '5,15,30,60,120,180') "check-in choices: $($vals -join ',')"
            "online over the live connection; offline $offAfter s after the service stopped; online again $onAfter s after it started (gap recorded); collect now picked up in $collectAfter s; installer lists in order (check-in $($vals -join '/') min)"
        } | Out-Null
    } else { Record 'T68' 'Live agent connection' 'BLOCKED' 'agent install failed' }
    Check 'T27' 'Agent port serves only the agent API and requires a client certificate' {
        $r1 = Invoke-WebRequest 'https://localhost:8444/' -SkipHttpErrorCheck
        $r2 = Invoke-WebRequest 'https://localhost:8444/api/v1/setup/status' -SkipHttpErrorCheck
        $r3 = Invoke-WebRequest 'https://localhost:8444/api/agent/v1/checkin' -Method Post -Body '{}' -ContentType 'application/json' -SkipHttpErrorCheck
        $r4 = Invoke-WebRequest "$base/api/agent/v1/checkin" -Method Post -Body '{}' -ContentType 'application/json' -SkipHttpErrorCheck
        Assert ($r1.StatusCode -eq 404) "console on agent port: $($r1.StatusCode)"
        Assert ($r2.StatusCode -eq 404) "API v1 on agent port: $($r2.StatusCode)"
        Assert ($r3.StatusCode -eq 401) "agent API without certificate: $($r3.StatusCode)"
        Assert ($r4.StatusCode -eq 404) "agent API on console port: $($r4.StatusCode)"
        $fw = Get-NetFirewallRule -DisplayName 'Zentriq Agent gateway*' -ErrorAction SilentlyContinue
        Assert $fw 'agent firewall rule missing'
        '/ 404, /api/v1 404, no cert 401, console port 404, firewall rule present'
    } | Out-Null

    $scanUser = 'zqscan'
    $scanPassword = 'Zs-' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(15)).Replace('+', 'x').Replace('/', 'y') + '-7b'
    Check 'T28' 'Agentless WinRM inventory with a stored credential; merges with the agent record' {
        # Target preparation as an administrator would do it (local account, WinRM, UAC remote restrictions, TrustedHosts for NTLM by address).
        New-LocalUser $scanUser -Password (ConvertTo-SecureString $scanPassword -AsPlainText -Force) -PasswordNeverExpires | Out-Null
        Add-LocalGroupMember -Group Administrators -Member $scanUser
        Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
        Set-Item WSMan:\localhost\Client\TrustedHosts -Value '*' -Force
        Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name LocalAccountTokenFilterPolicy -Value 1 -Type DWord
        # CI only: the scan targets this same machine; NTLM's loopback check rejects authentication to the machine's own
        # DNS aliases (e.g. <name>.mshome.net from reverse DNS). Remote targets are not affected.
        Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name DisableLoopbackCheck -Value 1 -Type DWord
        $ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -match '^(10\.|172\.(1[6-9]|2\d|3[01])\.|192\.168\.)' -and $_.PrefixOrigin -ne 'WellKnown' } | Select-Object -First 1).IPAddress
        Assert $ip 'no private IPv4 address on this machine'
        $s = Login
        $before = (Api GET '/assets/summary' -Session $s).total
        $cred = Api POST '/credentials' @{ name = 'ci-local-admin'; kind = 'windows_local'; username = "$env:COMPUTERNAME\$scanUser"; secret = @{ password = $scanPassword } } -Session $s
        $script:ciCred = $cred.id
        $list = Invoke-WebRequest "$base/api/v1/credentials" -WebSession $s
        Assert ($list.Content -notmatch [regex]::Escape($scanPassword)) 'credential API returned the secret'
        $scan = Api POST '/discovery/scans' @{ name = 'ci-winrm'; scanType = 'single'; targetsText = $ip; credentialIds = @($cred.id) } -Session $s
        $deadline = (Get-Date).AddMinutes(6)
        do { Start-Sleep 5; $d = Api GET "/discovery/scans/$($scan.id)" -Session $s } while ($d.scan.status -in 'queued', 'running' -and (Get-Date) -lt $deadline)
        $res = (Api GET "/discovery/scans/$($scan.id)/results" -Session $s).items | Select-Object -First 1
        Assert ($res.status -eq 'success') "scan result $($res.status): $($res.detail)"
        Assert ($res.protocol -like 'WinRM*') "protocol $($res.protocol)"
        $inv = Api GET "/assets/$($res.assetId)/inventory" -Session $s
        $diag = "detail: $($res.detail); document errors: $(@($inv.document.errors) -join ' | '); host: $($inv.document.device.hostname)"
        Assert ($inv.source -eq 'winrm') "inventory source $($inv.source); $diag"
        Assert (@($inv.software).Count -gt 5) "only $(@($inv.software).Count) software items; $diag"
        $after = (Api GET '/assets/summary' -Session $s).total
        if ($script:agentAsset) {
            Assert ($res.assetId -eq $script:agentAsset) "agentless scan created a second asset ($($res.assetId)) instead of updating the agent's ($($script:agentAsset))"
            Assert ($after -eq $before) "asset count $before -> $after"
        }
        "$ip via $($res.protocol): $(@($inv.software).Count) applications; assets $before -> $after; $($res.detail)"
    } | Out-Null
    function Pick-Service($AssetId, $Session) {
        $inv = Api GET "/assets/$AssetId/inventory" -Session $Session
        foreach ($n in 'W32Time', 'Spooler', 'WSearch') {
            $svc = @($inv.document.services) | Where-Object { $_.name -eq $n -and $_.state -eq 'Running' } | Select-Object -First 1
            if ($svc) { return $svc.name }
        }
        return $null
    }
    function Wait-Action($AssetId, $ActionId, $Session, [int] $Seconds = 240, [scriptblock] $Nudge = $null) {
        $deadline = (Get-Date).AddSeconds($Seconds)
        do {
            if ($Nudge) { & $Nudge }
            Start-Sleep 5
            $a = @(Api GET "/assets/$AssetId/actions" -Session $Session | ForEach-Object { $_ }) | Where-Object { $_.id -eq $ActionId } | Select-Object -First 1
        } while ((-not $a -or $a.status -notin 'succeeded', 'failed') -and (Get-Date) -lt $deadline)
        if (-not $a) { throw "action $ActionId not found" }
        return $a
    }

    Check 'T41' 'Asset type and address: VM recorded as virtual machine, IPv4 not IPv4-mapped IPv6, logged-on user present' {
        $s = Login
        Assert $script:agentAsset 'no agent asset from T26'
        $a = Api GET "/assets/$($script:agentAsset)" -Session $s
        Assert ($a.ip -and $a.ip -notlike '::ffff:*') "asset IP '$($a.ip)'"
        Assert (@($a.ipv4).Count -ge 1 -and @($a.ipv4)[0] -match '^\d+\.\d+\.\d+\.\d+$') "ipv4: $($a.ipv4 -join ',')"
        Assert ($null -eq $a.ipv6) 'IPv6 shown although display.show_ipv6 is off'
        Assert ($a.isVirtual -eq $true -and $a.chassis -eq 'virtual') "type: chassis=$($a.chassis) virtual=$($a.isVirtual) (CI runs on an Azure/Hyper-V VM)"
        Api PUT '/system/settings/display.show_ipv6' @{ value = $true } -Session $s | Out-Null
        $a6 = Api GET "/assets/$($script:agentAsset)" -Session $s
        Api PUT '/system/settings/display.show_ipv6' @{ value = $false } -Session $s | Out-Null
        Assert ($null -ne $a6.ipv6) 'IPv6 list missing after enabling display.show_ipv6'
        $o = Api GET '/dashboard/overview' -Session $s
        Assert ($o.kpi.assets -ge 1 -and @($o.byCategory).Count -ge 1) 'dashboard overview empty'
        "ip $($a.ip); type $($a.chassis); user '$($a.loggedOnUser)'; ipv6 toggle works; dashboard: $($o.kpi.assets) assets"
    } | Out-Null

    Check 'T42' 'Collect inventory now for all assets' {
        $s = Login
        $r = Api POST '/assets/collect' @{ all = $true } -Session $s
        Assert ($r.agents -ge 1 -or $r.agentless -ge 1) "nothing collected: $($r.message)"
        "agents $($r.agents), agentless $($r.agentless): $($r.message)"
    } | Out-Null

    if ($agentInstalled) {
        Check 'T43' 'Restart a Windows service through the Zentriq Agent' {
            $s = Login
            $svc = Pick-Service $script:agentAsset $s
            Assert $svc 'no suitable running service in the inventory'
            $r = Api POST "/assets/$($script:agentAsset)/services/$svc/restart" @{} -Session $s
            Assert ($r.channel -eq 'agent') "channel $($r.channel)"
            $exe = Join-Path $env:ProgramFiles 'Zentriq Agent\ZentriqAgent.exe'
            Stop-Service ZentriqAgent   # run check-ins in the foreground instead of waiting for the 2-minute interval
            try { $a = Wait-Action $script:agentAsset $r.id $s 240 { & $exe --once *> $null } } finally { Start-Service ZentriqAgent }
            Assert ($a.status -eq 'succeeded') "action $($a.status): $($a.detail)"
            Assert ((Get-Service $svc).Status -eq 'Running') "$svc is $((Get-Service $svc).Status) after restart"
            $p = Invoke-WebRequest "$base/api/v1/assets/$($script:agentAsset)/services/ZentriqAgent/stop" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body '{}' -SkipHttpErrorCheck
            Assert ($p.StatusCode -eq 409) "stopping ZentriqAgent returned $($p.StatusCode)"
            "$svc restarted by the agent ($($a.detail)); protected service refused"
        } | Out-Null
    } else { Record 'T43' 'Service restart through the agent' 'BLOCKED' 'agent install failed' }

    if ($agentInstalled) {
        Check 'T45' 'Uninstall software through the Zentriq Agent (single device), recorded in the Activity log' {
            $s = Login
            $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CiDummyApp'
            New-Item $key -Force | Out-Null
            Set-ItemProperty $key DisplayName 'CI Dummy App'
            Set-ItemProperty $key DisplayVersion '1.0'
            Set-ItemProperty $key Publisher 'Zentriq CI'
            Set-ItemProperty $key UninstallString 'cmd.exe /c exit 1'
            Set-ItemProperty $key QuietUninstallString ('"' + $env:SystemRoot + '\System32\reg.exe" delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CiDummyApp" /f')
            $exe = Join-Path $env:ProgramFiles 'Zentriq Agent\ZentriqAgent.exe'
            Api POST '/assets/collect' @{ assetIds = @($script:agentAsset) } -Session $s | Out-Null
            Stop-Service ZentriqAgent
            try {
                $deadline = (Get-Date).AddMinutes(4)
                do { & $exe --once *> $null; Start-Sleep 3; $inst = @(Api GET '/inventory/software/installs?name=CI%20Dummy%20App' -Session $s | ForEach-Object { $_ }) } while ($inst.Count -eq 0 -and (Get-Date) -lt $deadline)
                Assert ($inst.Count -ge 1) 'dummy application not in the inventory'
                Assert ($inst[0].agent -eq 'ready') "agent state $($inst[0].agent)"
                $r = Api POST '/software/uninstall' @{ name = 'CI Dummy App'; assetIds = @($script:agentAsset) } -Session $s
                Assert ($r.sent -eq 1) "sent $($r.sent): $($r.message)"
                $p = Invoke-WebRequest "$base/api/v1/software/uninstall" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body '{"name":"Zentriq Agent","all":true}' -SkipHttpErrorCheck
                Assert ($p.StatusCode -eq 409) "uninstalling the Zentriq Agent returned $($p.StatusCode)"
                $deadline = (Get-Date).AddMinutes(4)
                do { & $exe --once *> $null; Start-Sleep 3; $a = (Api GET "/activity?kind=uninstall&assetId=$($script:agentAsset)" -Session $s).items | Select-Object -First 1 } while ($a.status -notin 'succeeded', 'failed' -and (Get-Date) -lt $deadline)
            } finally { Start-Service ZentriqAgent }
            Assert ($a.status -eq 'succeeded') "uninstall $($a.status): $($a.detail)"
            Assert (-not (Test-Path $key)) 'uninstall registration still present'
            $left = @(Api GET '/inventory/software/installs?name=CI%20Dummy%20App' -Session $s | ForEach-Object { $_ })
            Assert ($left.Count -eq 0) 'software still listed after uninstall'
            "uninstalled via agent: $($a.detail); protected software refused; removed from inventory"
        } | Out-Null
    } else { Record 'T45' 'Software uninstall through the agent' 'BLOCKED' 'agent install failed' }

    Check 'T46' 'Dashboard drill-down filters and offline timeline on assets' {
        $s = Login
        $o = Api GET '/dashboard/overview' -Session $s
        Assert ($null -ne $o.disk -and $null -ne $o.cores -and $null -ne $o.seen) 'overview lacks disk/cores/seen'
        $v = Api GET '/assets?facet=type&value=virtual' -Session $s
        Assert (@($v.items | Where-Object id -eq $script:agentAsset).Count -eq 1) 'type=virtual drill-down does not list the VM'
        $c = (@($o.cores) | Select-Object -First 1)
        if ($c) { $cl = Api GET "/assets?facet=cores&value=$([uri]::EscapeDataString($c.key))" -Session $s; Assert ($cl.total -eq $c.count) "cores '$($c.key)': chart $($c.count) vs list $($cl.total)" }
        $a = Api GET "/assets/$($script:agentAsset)" -Session $s
        Assert ($null -ne $a.lastContactAt -and $null -ne $a.offlineDays) 'no last-contact fields'
        $old = Api GET '/assets?offlineDays=7' -Session $s
        Assert (@($old.items | Where-Object id -eq $script:agentAsset).Count -eq 0) 'recently seen asset listed as offline 7+ days'
        "drill-down by type and cores matches the charts; offlineDays $($a.offlineDays), online $($a.online)"
    } | Out-Null

    if ($agentInstalled) {
        Check 'T67' 'Agent uninstall protection: removal needs a one-hour code from the console; service stop held for a code (starts again without one); reinstall' {
            $s = Login
            $exe = Join-Path $env:ProgramFiles 'Zentriq Agent\ZentriqAgent.exe'
            Api PUT '/agents/settings' @{ checkinMinutes = 30; inventoryMinutes = 180; uninstallProtection = $true } -Session $s | Out-Null
            # The agent service (SYSTEM) turns protection on at its next check-in (every 2 minutes here).
            $deadline = (Get-Date).AddMinutes(5)
            do { Start-Sleep 10; $reg = Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent' } while (-not $reg.PSObject.Properties['UninstallProtected'] -and (Get-Date) -lt $deadline)
            Assert ($reg.PSObject.Properties['UninstallProtected'] -and $reg.UninstallProtected -eq 1) 'agent did not turn uninstall protection on'
            $readable = $true; try { Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent\Protection' -ErrorAction Stop | Out-Null } catch { $readable = $false }
            Assert (-not $readable) 'the uninstall key is readable by administrators'
            # A stop request is held for a code (Windows cannot refuse it); without one the agent starts again at once.
            $pid0 = (Get-CimInstance Win32_Service -Filter "Name='ZentriqAgent'").ProcessId
            & sc.exe stop ZentriqAgent | Out-Null
            Start-Sleep 3
            $held = (Get-Service ZentriqAgent).Status
            Assert ($held -eq 'StopPending') "a stop request was not held for a code: $held"
            $cp = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ZentriqAgentControl', [System.IO.Pipes.PipeDirection]::InOut); $cp.Connect(5000)
            $cw = New-Object System.IO.StreamWriter($cp); $cw.AutoFlush = $true; $cw.WriteLine('{"action":"cancel-stop"}'); (New-Object System.IO.StreamReader($cp)).ReadLine() | Out-Null; $cp.Dispose()
            $deadline = (Get-Date).AddSeconds(90)
            do { Start-Sleep 3; $sv = Get-CimInstance Win32_Service -Filter "Name='ZentriqAgent'" } while (-not ($sv.State -eq 'Running' -and $sv.ProcessId -ne $pid0) -and (Get-Date) -lt $deadline)
            Assert ($sv.State -eq 'Running' -and $sv.ProcessId -ne $pid0) "the agent did not start again after a stop without a code: $($sv.State)"
            $product = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Zentriq Agent' } | Select-Object -First 1).PSChildName
            $p1 = Start-Process msiexec.exe -ArgumentList '/x', $product, '/qn', '/l*v', "`"$(Join-Path $OutDir 'agent-uninstall-nocode.log')`"" -Wait -PassThru
            Assert ($p1.ExitCode -ne 0 -and (Get-Service ZentriqAgent -ErrorAction SilentlyContinue)) "uninstall without a code was not refused (exit $($p1.ExitCode))"
            $p2 = Start-Process msiexec.exe -ArgumentList '/x', $product, '/qn', 'UNINSTALLCODE=ABCD-EFGH-JKMN-PQRS', '/l*v', "`"$(Join-Path $OutDir 'agent-uninstall-badcode.log')`"" -Wait -PassThru
            Assert ($p2.ExitCode -ne 0 -and (Get-Service ZentriqAgent -ErrorAction SilentlyContinue)) "uninstall with a wrong code was not refused (exit $($p2.ExitCode))"
            # One random code for all agents, valid for one hour; online agents receive it over their live connection.
            $first = Api POST '/agents/uninstall-code' @{} -Session $s
            $code = Api POST '/agents/uninstall-code' @{} -Session $s
            Assert ($code.code -match '^[0-9A-Z]{4}(-[0-9A-Z]{4}){3}$' -and $code.code -ne $first.code -and ([datetime]$code.expiresAt).ToUniversalTime() -lt (Get-Date).ToUniversalTime().AddMinutes(61)) "uninstall code: $($code | ConvertTo-Json -Compress)"
            $shown = Api GET '/agents/uninstall-code' -Session $s
            Assert ($shown.active -and $shown.code -eq $code.code) "current code not shown: $($shown | ConvertTo-Json -Compress)"
            Start-Sleep 10
            $p2b = Start-Process msiexec.exe -ArgumentList '/x', $product, '/qn', "UNINSTALLCODE=$($first.code)", '/l*v', "`"$(Join-Path $OutDir 'agent-uninstall-oldcode.log')`"" -Wait -PassThru
            Assert ($p2b.ExitCode -ne 0 -and (Get-Service ZentriqAgent -ErrorAction SilentlyContinue)) "the replaced code still removed the agent (exit $($p2b.ExitCode))"
            # ---- T78: stopping and disabling need a code too (checked here while protection is on) ----
            Check 'T78' 'Agent stop and disable protection: a stop request waits for a code (dialog); without one the agent starts again at once; disabling refused; wrong code refused and reported; stop / disable / enable with a code; console pause, resume and per-device code' {
                $svcKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\ZentriqAgent'
                function Svc { Get-CimInstance Win32_Service -Filter "Name='ZentriqAgent'" }
                function WaitFor([scriptblock] $Cond, [int] $Seconds, [string] $What) {
                    $deadline = (Get-Date).AddSeconds($Seconds)
                    while (-not (& $Cond) -and (Get-Date) -lt $deadline) { Start-Sleep 3 }
                    Assert (& $Cond) "timed out: $What"
                }
                function Refused([scriptblock] $Do) { try { & $Do; $false } catch { $true } }
                function Hardened { ((& sc.exe sdshow ZentriqAgent) -join '') -match [regex]::Escape('(A;;CCLCSWRPWPLOCRRC;;;BA)') }
                # What the code dialog sends to the service over the local control pipe.
                function PipeSend([string] $Action, [string] $Code) {
                    $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ZentriqAgentControl', [System.IO.Pipes.PipeDirection]::InOut)
                    $p.Connect(5000)
                    $w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true
                    $r = New-Object System.IO.StreamReader($p)
                    $w.WriteLine((@{ action = $Action; code = $Code } | ConvertTo-Json -Compress))
                    $line = $r.ReadLine(); $p.Dispose()
                    Assert $line "no answer on the control pipe to '$Action'"
                    $line | ConvertFrom-Json
                }
                # Stop request (as Stop / Restart in the Services console): held ("Stopping") while the code dialog is open;
                # with the code (what the dialog sends over the local pipe) it stops or restarts; without one it stops and
                # starts again at once (Windows does not let a service refuse a stop).
                function StopRequest([string] $Code, [switch] $Restart, [switch] $WrongFirst) {
                    $script:pidBefore = (Svc).ProcessId
                    $out = (& sc.exe stop ZentriqAgent 2>&1) -join ' '
                    Assert ($LASTEXITCODE -eq 0) "sc stop failed: $out"
                    Start-Sleep 3
                    $st = (Get-Service ZentriqAgent).Status
                    Assert ($st -eq 'StopPending') "stop request not held for a code: $st"
                    $script:dialogs = @(Get-CimInstance Win32_Process -Filter "Name='ZentriqAgent.exe'" | Where-Object CommandLine -match '--stop-prompt').Count
                    if ($WrongFirst) {
                        $bad = PipeSend 'authorize-stop' 'ABCD-EFGH-JKMN-PQRS'
                        Assert (-not $bad.ok) 'a wrong code was accepted in the stop dialog'
                    }
                    if ($Code) {
                        $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ZentriqAgentControl', [System.IO.Pipes.PipeDirection]::InOut); $p.Connect(5000)
                        $w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($p)
                        $w.WriteLine((@{ action = 'authorize-stop'; code = $Code; restart = [bool]$Restart } | ConvertTo-Json -Compress))
                        $ok = $r.ReadLine() | ConvertFrom-Json; $p.Dispose()
                        Assert ($ok.ok) "code refused in the stop dialog: $($ok.message)"
                    } else { $c = PipeSend 'cancel-stop' $null; Assert ($c.ok) 'cancel not confirmed' }
                    return $st
                }
                function StartedAgain([string] $What) {
                    WaitFor { $v = Svc; $v.State -eq 'Running' -and $v.ProcessId -ne $script:pidBefore } 90 $What
                }
                # Wait for the agent to have applied tamper protection (registry key read-only for administrators).
                WaitFor { Refused { Set-ItemProperty $svcKey -Name Description -Value 'x' -ErrorAction Stop } } 180 'service registry key still writable for administrators'
                Assert (Refused { Set-Service ZentriqAgent -StartupType Disabled -ErrorAction Stop }) 'an administrator could disable the protected service'
                # Stop without a code (cancelled in the dialog): it stops and starts again at once.
                $first = StopRequest $null -WrongFirst
                StartedAgain 'the agent did not start again after a stop request without a code'
                WaitFor { Hardened } 120 'protection not back after a stop without a code'
                $script:stopNoCode = "stop without a code held ($first), wrong code refused, cancelled: started again at once (new process)"
                & sc.exe config ZentriqAgent start= disabled | Out-Null
                Assert ((Svc).StartMode -eq 'Auto') "startup type changed with sc.exe: $((Svc).StartMode)"
                Assert (Refused { Set-ItemProperty $svcKey -Name Start -Value 4 -ErrorAction Stop }) 'the Start value could be set to Disabled in the registry'
                Assert (Get-ScheduledTask -TaskPath '\Zentriq\' -TaskName 'Zentriq Agent watchdog' -ErrorAction SilentlyContinue) 'watchdog task missing'
                $folderWritable = -not (Refused { Set-Content (Join-Path $env:ProgramFiles 'Zentriq Agent\zq-test.txt') 'x' -ErrorAction Stop })
                Assert (-not $folderWritable) 'the agent program folder is writable for administrators'
                # A wrong code is refused and reported to the console.
                & $exe --stop ABCD-EFGH-JKMN-PQRS 2>&1 | Out-Null
                Assert ($LASTEXITCODE -ne 0 -and (Get-Service ZentriqAgent).Status -eq 'Running') 'a wrong code stopped the agent'
                WaitFor { $a = @(Api GET '/agents' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $script:agentId; $a.tamper.lastAt } 120 'refused code not reported to the console'
                $aud = Api GET '/audit?action=agent.tamper' -Session $s
                Assert ($aud.total -ge 1) 'agent.tamper not in the audit log'
                # Stop request with the code entered in the dialog (when an administrator's desktop is available): stops.
                $dev = Api POST "/agents/$($script:agentId)/code" -Session $s
                # Restart with the code: the service restarts by itself (new process).
                StopRequest $dev.code -Restart | Out-Null
                StartedAgain 'the agent did not restart after the code was entered in the dialog'
                WaitFor { Hardened } 120 'protection not back after the dialog restart'
                # Stop with the code: stops until started again.
                StopRequest $dev.code | Out-Null
                WaitFor { (Get-Service ZentriqAgent).Status -eq 'Stopped' } 60 'the agent did not stop after the code was entered in the dialog'
                Start-Service ZentriqAgent
                WaitFor { (Get-Service ZentriqAgent).Status -eq 'Running' } 60 'agent did not start again'
                WaitFor { Hardened } 120 'protection not back after the dialog stop'
                $script:stopDialog = "dialog shown in $($script:dialogs) session(s); wrong code refused; code + Restart restarted it (new process), code + Stop stopped it"
                # Stop with this device's own code; enable again.
                & $exe --stop $dev.code | Out-Host
                Assert ($LASTEXITCODE -eq 0) "--stop with the device code exit $LASTEXITCODE"
                WaitFor { (Get-Service ZentriqAgent).Status -eq 'Stopped' } 60 'agent did not stop with the device code'
                & $exe --enable | Out-Host
                WaitFor { (Get-Service ZentriqAgent).Status -eq 'Running' } 60 'agent did not start with --enable'
                WaitFor { Hardened } 120 'protection not back after the agent started again'
                # Disable with the code for all agents; enable again.
                & $exe --disable $code.code | Out-Host
                Assert ($LASTEXITCODE -eq 0) "--disable exit $LASTEXITCODE"
                WaitFor { (Get-Service ZentriqAgent).Status -eq 'Stopped' -and (Svc).StartMode -eq 'Disabled' } 60 'agent not disabled with the code'
                & $exe --enable | Out-Host
                WaitFor { (Get-Service ZentriqAgent).Status -eq 'Running' -and (Svc).StartMode -eq 'Auto' } 60 'agent not enabled again'
                WaitFor { Hardened } 120 'protection not back after enable'
                # From the console: pause (Services may stop it), then resume (refused again).
                Api POST "/agents/$($script:agentId)/control" @{ operation = 'pause'; hours = 1 } -Session $s | Out-Null
                WaitFor { -not (Hardened) } 180 'console pause did not relax the service permissions'
                Stop-Service ZentriqAgent -ErrorAction Stop   # allowed while paused
                Start-Service ZentriqAgent
                WaitFor { $a = @(Api GET '/agents' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $script:agentId; $a.tamper.state -eq 'paused' -and $a.online } 180 'paused state not reported'
                Api POST "/agents/$($script:agentId)/control" @{ operation = 'resume' } -Session $s | Out-Null
                WaitFor { Hardened } 180 'console resume did not restore protection'
                StopRequest $null | Out-Null
                StartedAgain 'the agent did not start again after a stop without a code (after resume)'
                WaitFor { Hardened } 120 'protection not back after the stop following resume'
                $acts = Api GET '/activity?kind=agent-control' -Session $s
                Assert (@($acts.items | Where-Object status -eq 'succeeded').Count -ge 2) 'console pause / resume not completed in the Activity log'
                "$($script:stopNoCode); $($script:stopDialog); disabling (Set-Service, sc.exe, registry) refused; program folder read-only; watchdog task present; wrong code refused and reported; stop with the device code and --enable; disable with the code for all and --enable; protection back each time; console pause allowed Stop-Service, resume held stop requests again"
            } | Out-Null

            $p3 = Start-Process $exe -ArgumentList '--uninstall', $code.code -Wait -PassThru
            Assert ($p3.ExitCode -eq 0) "--uninstall did not start the removal (exit $($p3.ExitCode))"
            $deadline = (Get-Date).AddMinutes(3)
            while ((Get-Service ZentriqAgent -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep 3 }
            Start-Sleep 5
            $unlog = Join-Path ([IO.Path]::GetTempPath()) 'ZentriqAgent-uninstall.log'
            if (Test-Path $unlog) { Copy-Item $unlog (Join-Path $OutDir 'agent-uninstall-code.log') -Force }
            Assert (-not (Get-Service ZentriqAgent -ErrorAction SilentlyContinue)) 'uninstall with the console code did not remove the agent (see agent-uninstall-code.log)'
            
            # Reinstall for the remaining tests, then switch protection off again.
            $tok = Api POST '/agents/enrollment-tokens' @{ name = 'ci-reinstall'; validDays = 1; maxUses = 1; serverUrl = 'https://localhost:8444' } -Session $s
            $p4 = Start-Process msiexec.exe -ArgumentList '/i', "`"$agentMsi`"", '/qn', "SERVERURL=https://localhost:8444", "ENROLLMENTCODE=$($tok.code)", 'CHECKININTERVAL=2', 'INVENTORYINTERVAL=30', '/l*v', "`"$(Join-Path $OutDir 'agent-reinstall.log')`"" -Wait -PassThru
            Assert ($p4.ExitCode -eq 0) "reinstall exit $($p4.ExitCode)"
            $deadline = (Get-Date).AddMinutes(4)
            do { Start-Sleep 5; $a = @(Api GET '/agents' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.status -eq 'active' -and $_.assetId -and $_.lastInventoryAt -and $_.id -ne $script:agentId } | Select-Object -First 1 } while (-not $a -and (Get-Date) -lt $deadline)
            Assert $a 'reinstalled agent did not enroll'
            $script:agentId = $a.id; $script:agentAsset = $a.assetId
            Api PUT '/agents/settings' @{ checkinMinutes = 30; inventoryMinutes = 180; uninstallProtection = $false } -Session $s | Out-Null
            $deadline = (Get-Date).AddMinutes(5)
            do { Start-Sleep 10 } while ((Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent').PSObject.Properties['UninstallProtected'] -and (Get-Date) -lt $deadline)
            Assert (-not (Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent').PSObject.Properties['UninstallProtected']) 'protection still on after switching it off'
            Stop-Service ZentriqAgent; Start-Service ZentriqAgent   # stoppable again
            "no code: exit $($p1.ExitCode); wrong code: exit $($p2.ExitCode); stop without a code: held, started again; code for all agents $($code.code) (valid until $($code.expiresAt)) removed the agent, the replaced code did not; reinstalled and protection switched off"
        } | Out-Null
    } else { Record 'T67' 'Agent uninstall protection' 'BLOCKED' 'agent install failed' }

    # ---- Software Deployment: approved software, application control, push deployment (Windows agent) ------------
    if ($agentInstalled) {
        function Dep-Wait([scriptblock] $Cond, [int] $Seconds, [string] $What) {
            $deadline = (Get-Date).AddSeconds($Seconds)
            do { $v = & $Cond; if ($v) { return $v }; Start-Sleep 4 } while ((Get-Date) -lt $deadline)
            throw "timed out after $Seconds s: $What"
        }
        function Dep-Device($s) { @(Api GET '/deployment/devices' -Session $s | ForEach-Object { $_ }) | Where-Object agentId -eq $script:agentId | Select-Object -First 1 }
        function Dep-Upload($s, [string] $File) {
            Invoke-RestMethod "$base/api/v1/deployment/packages/upload?fileName=$([uri]::EscapeDataString((Split-Path $File -Leaf)))" -Method Post -InFile $File `
                -ContentType 'application/octet-stream' -Headers @{ 'X-Zentriq-Request' = '1' } -WebSession $s
        }
        $agentExe = Join-Path $env:ProgramFiles 'Zentriq Agent\ZentriqAgent.exe'
        $depOk = Check 'T82' 'Software Deployment: standard catalogue, approve from the catalogue and from inventory, block rule, test, settings; the policy reaches the agent at once' {
            $s = Login
            $sum = Api GET '/deployment/summary' -Session $s
            Assert ($sum.settings.mode -eq 'off') "application control should be off by default: $($sum.settings.mode)"
            Assert ($sum.catalog.entries -ge 150) "standard catalogue: $($sum.catalog.entries) entries"
            Assert ($sum.settings.message -like 'This is unauthorized software. Please contact your administrator*') "default message: $($sum.settings.message)"
            $cat = Api GET '/deployment/catalog' -Session $s
            Assert (@($cat.entries | Where-Object key -eq 'google-chrome').Count -eq 1) 'Google Chrome missing from the catalogue'
            $r = Api POST '/deployment/catalog/approve' @{ keys = @('google-chrome', '7-zip'); action = 'allow' } -Session $s
            Assert ($r.created -eq 4) "catalogue approval created $($r.created) rules (expected 4: Windows and Linux each)"
            $again = Api POST '/deployment/catalog/approve' @{ keys = @('google-chrome'); action = 'allow' } -Session $s
            Assert ($again.created -eq 0) 'approving again created duplicate rules'
            $t1 = Api POST '/deployment/rules/test' @{ name = 'Google Chrome'; publisher = 'Google LLC'; platform = 'windows' } -Session $s
            Assert ($t1.approved) "Google Chrome not approved: $($t1.reason)"
            $t2 = Api POST '/deployment/rules/test' @{ name = 'Some Game'; publisher = 'Games Inc'; platform = 'windows' } -Session $s
            Assert (-not $t2.approved -and $t2.effect -like 'installs (application control is off)*') "unknown software: $($t2 | ConvertTo-Json -Compress)"
            $bad = Api POST '/deployment/rules' @{ action = 'block'; name = 'Bad Tool*'; platform = 'windows' } -Session $s
            Assert ((Api POST '/deployment/rules/test' @{ name = 'Bad Tool 2'; platform = 'windows' } -Session $s).outcome -eq 'blocked') 'block rule not applied'
            $inv = @(Api GET '/deployment/inventory?status=unapproved&platform=windows' -Session $s | ForEach-Object { $_ })
            Assert ($inv.Count -gt 0) 'no unapproved software found on the runner'
            $fromInv = Api POST '/deployment/rules/from-inventory' @{ items = @($inv | ForEach-Object { @{ name = $_.name; publisher = $_.publisher; platform = $_.platform } }); withPublisher = $true } -Session $s
            Assert ($fromInv.created -ge 1) 'nothing approved from the inventory'
            $left = @(Api GET '/deployment/inventory?status=unapproved&platform=windows' -Session $s | ForEach-Object { $_ }).Count
            Assert ($left -eq 0) "$left item(s) still not approved after approving the inventory"
            $v = Api PUT '/deployment/settings' @{ mode = 'audit' } -Session $s
            Assert ($v.mode -eq 'audit' -and $v.enabledAt) 'audit mode not saved'
            $dev = Dep-Wait { $d = Dep-Device $s; if ($d -and $d.appControl -eq 'audit' -and $d.policyCurrent) { $d } } 120 'the agent to apply audit mode'
            $local = (& $agentExe --app-control) -join ' '
            Assert ($local -match 'Application control: audit') "on the device: $local"
            "catalogue $($sum.catalog.entries) entries; 4 rules from the catalogue (no duplicates); $($fromInv.created) approved from the inventory; block rule; audit mode applied by the agent within seconds ($local)"
        }

        $sessionId = (Get-Process -Id $PID).SessionId
        if ($depOk -and $sessionId -ne 0) {
            Check 'T83' 'Application control on Windows: an installer that is not approved is reported in audit mode, stopped with the message in block mode; the user requests approval from the message; once approved it runs' {
                $s = Login
                $setup = Join-Path $OutDir 'ZqDemoSetup.exe'
                Copy-Item (Join-Path $env:windir 'System32\PING.EXE') $setup -Force
                $sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
                function Run-Setup { Start-Process $setup -ArgumentList '-n', '25', '127.0.0.1' -PassThru -WindowStyle Hidden }
                # Audit: it runs and is reported.
                $p = Run-Setup; Start-Sleep 6
                Assert (-not $p.HasExited) 'audit mode stopped the installer'
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                $ev = Dep-Wait { @(Api GET '/deployment/events?kind=audited&days=1' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.path -like '*ZqDemoSetup.exe' -and $_.sha256 -eq $sha } | Select-Object -First 1 } 90 'the audited installation in Events'
                # Block for a device group with this device.
                $g = Api POST '/deployment/groups' @{ name = 'CI devices'; assetIds = @($script:agentAsset); appMode = 'block' } -Session $s
                $script:ciGroup = $g.id
                Dep-Wait { $d = Dep-Device $s; if ($d -and $d.appControl -eq 'block' -and $d.policyCurrent) { $d } } 120 'the agent to apply block mode (device group)' | Out-Null
                $p = Run-Setup
                $deadline = (Get-Date).AddSeconds(15)
                while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
                Assert ($p.HasExited) 'block mode did not stop the installer'
                $stopSec = [int]((Get-Date) - $p.StartTime).TotalSeconds
                $blk = Dep-Wait { @(Api GET '/deployment/events?kind=blocked&days=1' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.path -like '*ZqDemoSetup.exe' } | Select-Object -First 1 } 90 'the blocked installation in Events'
                # The message on the user's desktop; "Request approval" goes back to the agent over the notice's own pipe.
                $notice = Dep-Wait { Get-CimInstance Win32_Process -Filter "Name='ZentriqAgent.exe'" | Where-Object CommandLine -match '--app-notice' | Select-Object -First 1 } 20 'the message on the desktop'
                $details = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($notice.CommandLine -split '\s+')[-1])) | ConvertFrom-Json
                Assert ($details.message -like 'This is unauthorized software*') "message: $($details.message)"
                $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $details.pipe, [System.IO.Pipes.PipeDirection]::InOut); $pipe.Connect(5000)
                $w = New-Object System.IO.StreamWriter($pipe); $w.AutoFlush = $true; $rd = New-Object System.IO.StreamReader($pipe)
                $w.WriteLine('{"reason":"CI test: needed for the network check"}'); $ack = $rd.ReadLine(); $pipe.Dispose()
                Assert ($ack -match '"ok":true') "request not accepted by the agent: $ack"
                # One message per attempt: running it again while the message is open stops it without a second message.
                $p2 = Run-Setup
                $deadline = (Get-Date).AddSeconds(15)
                while (-not $p2.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
                Assert ($p2.HasExited) 'block mode did not stop the second attempt'
                Start-Sleep 3
                $open = @(Get-CimInstance Win32_Process -Filter "Name='ZentriqAgent.exe'" | Where-Object CommandLine -match '--app-notice')
                Assert ($open.Count -eq 1) "expected one message while it is open, found $($open.Count)"
                Stop-Process -Id $notice.ProcessId -Force -ErrorAction SilentlyContinue
                Start-Sleep 2
                Assert (-not (Get-CimInstance Win32_Process -Filter "Name='ZentriqAgent.exe'" | Where-Object CommandLine -match '--app-notice')) 'the message came back after it was closed'
                # Closed: a new attempt shows the message again.
                $p3 = Run-Setup
                $again = Dep-Wait { Get-CimInstance Win32_Process -Filter "Name='ZentriqAgent.exe'" | Where-Object CommandLine -match '--app-notice' | Select-Object -First 1 } 20 'the message for a new attempt'
                Stop-Process -Id $again.ProcessId -Force -ErrorAction SilentlyContinue
                # Closed, then started again by something other than a person (a script host here, as an updater or a
                # retrying bootstrapper would): stopped, but no new message.
                Start-Sleep 2
                $vbs = Join-Path $env:TEMP 'zq-ci-retry.vbs'
                Set-Content -LiteralPath $vbs -Encoding ASCII -Value ('CreateObject("WScript.Shell").Run """' + $setup + '"" -n 25 127.0.0.1", 0, False')
                Start-Process (Join-Path $env:WINDIR 'System32\wscript.exe') -ArgumentList "//B `"$vbs`"" -Wait
                Start-Sleep 6
                Assert (-not (Get-CimInstance Win32_Process -Filter "Name='ZentriqAgent.exe'" | Where-Object CommandLine -match '--app-notice')) 'the message came back for a retry not started by a person'
                Assert (-not (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($setup)) -ErrorAction SilentlyContinue)) 'the retried installer was not stopped'
                Remove-Item $vbs -ErrorAction SilentlyContinue
                $req = Dep-Wait { @(Api GET '/deployment/requests?status=pending' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.sha256 -eq $sha } | Select-Object -First 1 } 90 'the request in Requests'
                Assert ($req.reason -like 'CI test*' -and $req.user) "request: $($req | ConvertTo-Json -Compress)"
                # The request carries everything the device knows about the installer.
                $dt = $req.details
                Assert ($dt -and $dt.'SHA-256' -eq $sha -and $dt.'Installer type' -and $dt.'File name' -eq 'ZqDemoSetup.exe' -and $dt.'File size' -and $dt.'Signature' -and $dt.'Started by' -and $req.deviceOs) `
                    "request details: $($req | ConvertTo-Json -Compress -Depth 4)"
                # Approve exactly this file for this device: it runs.
                $ap = Api POST "/deployment/requests/$($req.id)/approve" @{ scope = 'device'; match = 'hash' } -Session $s
                Assert $ap.ruleId 'no rule from the approval'
                Dep-Wait { $d = Dep-Device $s; if ($d -and $d.policyCurrent) { $d } } 120 'the agent to receive the approval' | Out-Null
                Start-Sleep 3
                $p = Run-Setup; Start-Sleep 6
                Assert (-not $p.HasExited) 'the approved file was stopped'
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                $rq = @(Api GET '/deployment/requests?status=approved' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $req.id
                Assert ($rq.ruleId -eq $ap.ruleId) 'request not marked approved'
                "audit: ran and reported ($($ev.name)); block: stopped within $stopSec s, reported, message '$($details.message)' shown to the user, once per attempt; request '$($req.reason)' by $($req.user) with $(@($dt.PSObject.Properties).Count) details; approved for this device by SHA-256: runs"
            } | Out-Null
        } else { Record 'T83' 'Application control on Windows' 'NOT RUN' "the tests run in session $sessionId (services are not checked)" }

        if ($depOk) {
            Check 'T84' 'Push deployment: upload a package, install on the device with per-device status, a failing package and retry, uninstall; WDAC audit for a device group' {
                $s = Login
                $tool = Join-Path $OutDir 'zq-ci-tool-setup.exe'
                Copy-Item (Join-Path $env:windir 'System32\cmd.exe') $tool -Force
                $marker = Join-Path $env:ProgramData 'zq-ci-deploy.txt'
                Remove-Item $marker -ErrorAction SilentlyContinue
                $up = Dep-Upload $s $tool
                Assert ($up.format -eq 'exe' -and $up.sha256 -eq (Get-FileHash $tool -Algorithm SHA256).Hash.ToLowerInvariant()) "upload: $($up | ConvertTo-Json -Compress)"
                $pkg = Api POST '/deployment/packages' @{ uploadId = $up.uploadId; name = 'ZQ CI Tool'; version = '1.0'; installArgs = "/c echo deployed> `"$marker`""; detectName = 'ZQ CI Tool' } -Session $s
                $job = Api POST '/deployment/jobs' @{ packageId = $pkg.id; operation = 'install'; assetIds = @($script:agentAsset) } -Session $s
                Assert ($job.sent -eq 1 -and $job.skipped -eq 0) "job: $($job | ConvertTo-Json -Compress)"
                $d = Dep-Wait { $x = @(Api GET "/deployment/jobs/$($job.id)" -Session $s | ForEach-Object { $_ }) | Select-Object -First 1; if ($x.status -in 'succeeded', 'failed') { $x } } 180 'the installation on the device'
                Assert ($d.status -eq 'succeeded') "install: $($d.status) $($d.detail)"
                Assert ((Test-Path $marker) -and (Get-Content $marker -Raw) -match 'deployed') 'the package did not run on the device'
                # A package that fails, and a retry.
                $up2 = Dep-Upload $s $tool
                $bad = Api POST '/deployment/packages' @{ uploadId = $up2.uploadId; name = 'ZQ CI Broken'; version = '1.0'; installArgs = '/c exit 7'; approve = $false } -Session $s
                $job2 = Api POST '/deployment/jobs' @{ packageId = $bad.id; operation = 'install'; assetIds = @($script:agentAsset) } -Session $s
                $f = Dep-Wait { $x = @(Api GET "/deployment/jobs/$($job2.id)" -Session $s | ForEach-Object { $_ }) | Select-Object -First 1; if ($x.status -in 'succeeded', 'failed') { $x } } 180 'the failing installation'
                Assert ($f.status -eq 'failed' -and $f.detail -match 'exit code 7') "failing package: $($f.status) $($f.detail)"
                $rt = Api POST "/deployment/jobs/$($job2.id)/retry" -Session $s
                Assert ($rt.retried -eq 1) 'retry not queued'
                $jobs = @(Api GET '/deployment/jobs' -Session $s | ForEach-Object { $_ })
                Assert (@($jobs | Where-Object { $_.id -eq $job.id -and $_.succeeded -eq 1 }).Count -eq 1) 'job list does not show the success'
                # Uninstall (registered software): reported as done when it is not installed.
                $job3 = Api POST '/deployment/jobs' @{ packageId = $pkg.id; operation = 'uninstall'; assetIds = @($script:agentAsset) } -Session $s
                $u = Dep-Wait { $x = @(Api GET "/deployment/jobs/$($job3.id)" -Session $s | ForEach-Object { $_ }) | Select-Object -First 1; if ($x.status -in 'succeeded', 'failed') { $x } } 180 'the uninstall on the device'
                Assert ($u.status -eq 'succeeded') "uninstall: $($u.status) $($u.detail)"
                # WDAC in audit mode for the device group (or reported as not available on this edition).
                $wdac = 'not tried'
                if ($script:ciGroup) {
                    Api PUT "/deployment/groups/$($script:ciGroup)" @{ name = 'CI devices'; assetIds = @($script:agentAsset); appMode = 'audit'; wdacMode = 'audit' } -Session $s | Out-Null
                    $dw = Dep-Wait { $x = Dep-Device $s; if ($x -and $x.policyCurrent -and $x.wdac -in 'audit', 'unsupported', 'error') { $x } } 300 'WDAC audit on the device'
                    Assert ($dw.wdac -in 'audit', 'unsupported') "WDAC: $($dw.wdac) (see the agent log)"
                    $wdac = $dw.wdac
                    if ($wdac -eq 'audit') { Assert (Test-Path (Join-Path $env:windir 'System32\CodeIntegrity\CiPolicies\Active\{7A5C1E3B-2F4D-4B8E-9C6A-5D3E2F1A0B9C}.cip')) 'WDAC policy file missing' }
                    Api PUT "/deployment/groups/$($script:ciGroup)" @{ name = 'CI devices'; assetIds = @($script:agentAsset); appMode = 'audit'; wdacMode = 'off' } -Session $s | Out-Null
                    Dep-Wait { $x = Dep-Device $s; if ($x -and $x.policyCurrent -and $x.wdac -in 'off', 'unsupported') { $x } } 300 'WDAC off again' | Out-Null
                }
                "installed through the agent ($($d.detail)); failing package reported ($($f.detail)) and retried; uninstall done; WDAC for the group: $wdac"
            } | Out-Null
            # Back to off for the remaining tests.
            try {
                $s = Login
                Api PUT '/deployment/settings' @{ mode = 'off' } -Session $s | Out-Null
                if ($script:ciGroup) { Api DELETE "/deployment/groups/$($script:ciGroup)" -Session $s | Out-Null }
            } catch { Write-Host "Could not reset application control: $_" }
        } else { Record 'T84' 'Push deployment' 'BLOCKED' 'T82 failed' }

        # ---- T86: Software License Management ----
        Check 'T86' 'Software licenses: a product found in the inventory, a subscription with too few licences ending soon raises shortfall and expiry alerts, renewal resolves them; Windows activation reported by the agent' {
            $s = Login
            Api POST '/assets/collect' @{ assetIds = @($script:agentAsset) } -Session $s | Out-Null
            $deadline = (Get-Date).AddMinutes(6); $act = $null
            do { Start-Sleep 10; $act = Api GET '/sam/activation?filter=windows' -Session $s } while (@($act.rows | Where-Object assetId -eq $script:agentAsset).Count -eq 0 -and (Get-Date) -lt $deadline)
            $win = @($act.rows | Where-Object assetId -eq $script:agentAsset) | Select-Object -First 1
            Assert ($null -ne $win) 'the agent reported no Windows activation'
            $disc = Api GET '/sam/discover' -Session $s
            $title = @($disc.uncovered)[0]
            Assert ($null -ne $title) 'no installed software to track'
            $p = Api POST '/sam/products' @{ name = "CI $($title.name)"; namePatterns = @($title.name); metric = 'device' } -Session $s
            $l = Api POST '/sam/licenses' @{ productId = $p.id; name = 'CI subscription'; licenseType = 'subscription'; quantity = 0; endDate = (Get-Date).AddDays(5).ToString('yyyy-MM-dd'); unitCost = 10; currency = 'USD' } -Session $s
            Api POST '/sam/evaluate' -Session $s | Out-Null
            $d = Api GET "/sam/products/$($p.id)" -Session $s
            Assert ($d.compliance.status -eq 'shortfall') "position: $($d.compliance.status)"
            Assert ($d.compliance.devices -ge 1) 'no installations counted'
            $alerts = @(Api GET '/sam/alerts' -Session $s | ForEach-Object { $_ })
            Assert (@($alerts | Where-Object { $_.productId -eq $p.id -and $_.kind -eq 'shortfall' }).Count -eq 1) 'no shortfall alert'
            $exp = @($alerts | Where-Object { $_.licenseId -eq $l.id -and $_.kind -eq 'expiring' }) | Select-Object -First 1
            Assert ($exp -and $exp.severity -eq 'high') "expiry alert: $($exp | ConvertTo-Json -Compress)"
            $sum = Api GET '/sam/summary' -Session $s
            Assert ($sum.shortfall -ge 1 -and $sum.detail.expiring30 -ge 1) "summary: shortfall $($sum.shortfall), expiring $($sum.detail.expiring30)"
            $h = Api GET '/sam/highlights' -Session $s
            Assert ($h.licenses.shortfall -ge 1) 'dashboard highlights do not show the shortfall'
            Api POST "/sam/licenses/$($l.id)/renew" @{ endDate = (Get-Date).AddDays(365).ToString('yyyy-MM-dd'); quantity = 50 } -Session $s | Out-Null
            $open = @(Api GET '/sam/alerts' -Session $s | ForEach-Object { $_ } | Where-Object { $_.productId -eq $p.id -and $_.kind -in 'shortfall', 'expiring' })
            Assert ($open.Count -eq 0) "alerts still open after the renewal: $(($open | ForEach-Object { $_.kind }) -join ', ')"
            Assert ((Api GET "/sam/products/$($p.id)" -Session $s).compliance.status -in 'compliant', 'over_licensed') 'not compliant after the renewal'
            $inst = @(Api GET "/sam/products/$($p.id)/installations" -Session $s | ForEach-Object { $_ })
            Assert (@($inst | Where-Object assetId -eq $script:agentAsset).Count -eq 1) 'the runner is not listed among the installations'
            Api DELETE "/sam/products/$($p.id)" -Session $s | Out-Null
            "activation: $($win.product) $($win.status) ($($win.channel)); '$($title.name)' on $($d.compliance.devices) device(s): shortfall and expiry alerts, resolved by the renewal"
        } | Out-Null

        # ---- T87: Web Apps & Accounts ----
        Check 'T87' 'Web apps and accounts: add from the catalogue, import the user list, an account of a person who left is orphaned, unsanctioned use is reported' {
            $s = Login
            $cat = Api GET '/webapps/catalog' -Session $s
            Assert (@($cat.apps).Count -ge 60) "catalogue: $(@($cat.apps).Count) apps"
            $existing = @(Api GET '/webapps?q=Miro' -Session $s | ForEach-Object { $_ }) | Where-Object catalogKey -eq 'miro'
            if (-not $existing) { Api POST '/webapps/from-catalog' @{ keys = @('miro'); status = 'sanctioned' } -Session $s | Out-Null }
            $app = @(Api GET '/webapps?q=Miro' -Session $s | ForEach-Object { $_ }) | Where-Object catalogKey -eq 'miro' | Select-Object -First 1
            Assert ($null -ne $app) 'Miro not added'
            Api POST '/lifecycle/people' @{ name = 'CI Leaver'; email = 'ci.leaver@example.com'; status = 'left' } -Session $s | Out-Null
            $csv = "Email,Name,Role,Last Login`nci.leaver@example.com,CI Leaver,Admin,2026-01-05`nci.user@example.com,CI User,Member,$((Get-Date).ToString('yyyy-MM-dd'))"
            $imp = Api POST "/webapps/$($app.id)/accounts/import" @{ csv = $csv } -Session $s
            Assert ($imp.imported -eq 2) "imported $($imp.imported)"
            $orph = @(Api GET "/webapps/accounts?appId=$($app.id)&filter=orphaned" -Session $s | ForEach-Object { $_ })
            Assert ($orph.Count -eq 1 -and $orph[0].userName -eq 'ci.leaver@example.com') "orphaned: $(($orph | ForEach-Object { $_.userName }) -join ', ')"
            $alerts = @(Api GET '/webapps/alerts' -Session $s | ForEach-Object { $_ } | Where-Object { $_.appId -eq $app.id -and $_.kind -eq 'webapp_orphaned' })
            Assert ($alerts.Count -eq 1 -and $alerts[0].severity -eq 'high') 'no orphaned-account alert'
            $sum = Api GET '/webapps/summary' -Session $s
            Assert ($sum.orphaned -ge 1) "summary orphaned $($sum.orphaned)"
            # Web apps seen on the runner from its DNS cache (GitHub, Microsoft 365 …) after the inventory.
            $seen = @(Api GET '/webapps?filter=inuse' -Session $s | ForEach-Object { $_ })
            Api POST '/webapps/status' @{ ids = @($app.id); status = 'unsanctioned' } -Session $s | Out-Null
            Api DELETE "/webapps/$($app.id)" -Session $s | Out-Null
            "catalogue $(@($cat.apps).Count) apps; 2 accounts imported, the leaver's admin account orphaned with a high alert; in use on the runner: $(($seen | ForEach-Object { $_.name }) -join ', ')"
        } | Out-Null

        # ---- T88: network connected devices and IP address management ----
        Check 'T88' 'Network: connected devices register, IP address management, MAC vendor status and notifications respond' {
            $s = Login
            $sum = Api GET '/network/summary' -Session $s
            Assert ($null -ne $sum.endpoints -and $null -ne $sum.ipConflicts) 'summary lacks the connected devices'
            $ep = @(Api GET '/network/endpoints?filter=unknown' -Session $s | ForEach-Object { $_ })
            $subnets = @(Api GET '/network/subnets' -Session $s | ForEach-Object { $_ })
            $vs = Api GET '/network/vendors/status' -Session $s
            $notes = @(Api GET '/system/notifications' -Session $s | ForEach-Object { $_ })
            "connected devices $($sum.endpoints) ($($ep.Count) unknown); $($subnets.Count) subnet(s); $($vs.prefixes) MAC vendor prefixes; $($notes.Count) notification(s)"
        } | Out-Null

        # ---- T89: online warranty lookup ----
        Check 'T89' 'Warranty lookup: Dell, Lenovo and HP keys stored encrypted, a wrong key is explained, devices of other makers are skipped, expiry overview' {
            $s = Login
            $w = Api GET '/lifecycle/warranty' -Session $s
            Assert (@($w.providers).Count -eq 3) "providers: $(@($w.providers | ForEach-Object { $_.vendor }) -join ',')"
            Assert ($w.settings.refreshDays -eq 30) "refresh days $($w.settings.refreshDays)"
            $bad = Invoke-WebRequest "$base/api/v1/lifecycle/warranty/providers/dell" -Method Put -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body '{"clientId":"ci-only","enabled":true}' -SkipHttpErrorCheck
            Assert ($bad.StatusCode -eq 400 -and (Body $bad) -match 'secret') "Dell key without a secret: $($bad.StatusCode)"
            Api PUT '/lifecycle/warranty/providers/dell' @{ clientId = 'zentriq-ci-not-a-key'; secret = 'zentriq-ci-not-a-secret'; enabled = $true } -Session $s | Out-Null
            $w = Api GET '/lifecycle/warranty' -Session $s
            Assert (($w.providers | Where-Object vendor -eq 'dell').configured) 'Dell key not stored'
            $t = Api POST '/lifecycle/warranty/providers/dell/test' @{ serial = 'ABC1234' } -Session $s
            Assert (-not $t.ok -and $t.message.Length -gt 10) "test with a made-up key: $($t | ConvertTo-Json -Compress)"
            $lookup = 'no device'
            if ($script:agentAsset) {
                $r = Api POST '/lifecycle/warranty/lookup' @{ assetIds = @($script:agentAsset) } -Session $s
                Assert (-not $r.queued -and $r.found -eq 0 -and $r.checked -eq 0) "runner VM (not Dell/Lenovo/HP): $($r | ConvertTo-Json -Compress)"
                $one = Api GET "/lifecycle/warranty/assets/$($script:agentAsset)" -Session $s
                Assert ($null -eq $one.status) "runner VM looked up: $($one | ConvertTo-Json -Compress)"
                $lookup = "runner VM skipped ($($r.message))"
            }
            $all = Api POST '/lifecycle/warranty/lookup' @{ all = $true } -Session $s
            Assert $all.queued 'look up all not queued'
            $x = Api GET '/lifecycle/warranty/expiry' -Session $s
            Assert ($null -ne $x.total -and $null -ne $x.expired -and $null -ne $x.d30) "expiry overview: $($x | ConvertTo-Json -Compress)"
            Api PUT '/lifecycle/warranty/providers/dell' @{ clientId = ''; enabled = $false } -Session $s | Out-Null
            $w = Api GET '/lifecycle/warranty' -Session $s
            Assert (-not ($w.providers | Where-Object vendor -eq 'dell').configured) 'Dell key not removed'
            "made-up Dell key: $($t.message); $lookup; $($x.total) asset(s) in the expiry overview ($($x.unknown) without a warranty date)"
        } | Out-Null

        # ---- T90: remote session recording ----
        Check 'T90' 'Session recording: settings validated, the engine is configured to record, recordings list and playback protected, off again' {
            $s = Login
            $rs = Api GET '/remote/settings' -Session $s
            Assert ($rs.recordingRetentionDays -eq 90 -and -not $rs.recordSessions) "defaults: $($rs | ConvertTo-Json -Compress)"
            $rs.recordSessions = $true; $rs.recordAllDevices = $false; $rs.recordGroupIds = @()
            $bad = Invoke-WebRequest "$base/api/v1/remote/settings" -Method Put -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body ($rs | ConvertTo-Json -Depth 4) -SkipHttpErrorCheck
            Assert ($bad.StatusCode -eq 400) "groups required: $($bad.StatusCode)"
            $rs.recordAllDevices = $true; $rs.recordingRetentionDays = 30
            $put = Invoke-WebRequest "$base/api/v1/remote/settings" -Method Put -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body ($rs | ConvertTo-Json -Depth 4) -SkipHttpErrorCheck
            Assert ($put.StatusCode -in 204, 503) "save: $($put.StatusCode) $(Body $put)"
            Assert (Test-Path (Join-Path $dataDir 'Remote\cert\recording.txt')) 'recording.txt not written for the remote support service'
            $cfgFile = Join-Path $dataDir 'Remote\data\config.json'
            $applied = $false
            for ($i = 0; $i -lt 30 -and -not $applied; $i++) {
                if (Test-Path $cfgFile) { $applied = (Get-Content $cfgFile -Raw) -match '"sessionRecording"' }
                if (-not $applied) { Start-Sleep 5 }
            }
            Assert $applied 'ZentriqRemote did not add sessionRecording to the engine configuration within 150 s'
            Assert (Test-Path (Join-Path $dataDir 'Remote\recordings')) 'recordings folder missing'
            $list = Api GET '/remote/recordings' -Session $s
            Assert ($null -ne $list.total) 'recordings list'
            $sess = Api GET '/remote/sessions' -Session $s
            $missing = Invoke-WebRequest "$base/api/v1/remote/recordings/999999999/file" -WebSession $s -SkipHttpErrorCheck
            Assert ($missing.StatusCode -eq 404) "unknown recording: $($missing.StatusCode)"
            $anon = Invoke-WebRequest "$base/api/v1/remote/recordings/1/file" -SkipHttpErrorCheck
            Assert ($anon.StatusCode -in 401, 403) "playback without sign-in: $($anon.StatusCode)"
            $rs.recordSessions = $false
            $off = Invoke-WebRequest "$base/api/v1/remote/settings" -Method Put -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body ($rs | ConvertTo-Json -Depth 4) -SkipHttpErrorCheck
            Assert ($off.StatusCode -in 204, 503) "turn off: $($off.StatusCode)"
            Assert (-not (Test-Path (Join-Path $dataDir 'Remote\cert\recording.txt'))) 'recording.txt left after turning recording off'
            "engine configured to record (retention 30 days); $($list.total) recording(s), $($sess.total) session(s) in history; turned off again"
        } | Out-Null

        # ---- T91: console sign-in with Active Directory and Microsoft Entra ID ----
        Check 'T91' 'Directory sign-in: Sign In With Microsoft redirects with PKCE, a forged callback is refused, Active Directory errors are clear, group mappings, local sign-in keeps working' {
            # Sign-in endpoints are rate limited per address (10 a minute): wait out a 429.
            function Limited([scriptblock] $Call) { for ($i = 0; $i -lt 6; $i++) { $r = & $Call; if ($r.StatusCode -ne 429) { return $r }; Start-Sleep 15 }; $r }
            $s = Login
            $o = Invoke-RestMethod "$base/api/v1/auth/options"
            Assert (-not $o.microsoft -and $null -eq $o.directory) "options before: $($o | ConvertTo-Json -Compress)"
            $tenant = [guid]::NewGuid().ToString(); $client = [guid]::NewGuid().ToString()
            $bad = Invoke-WebRequest "$base/api/v1/auth/sign-in/entra" -Method Put -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body (@{ enabled = $true; tenantId = 'contoso'; clientId = $client } | ConvertTo-Json) -SkipHttpErrorCheck
            Assert ($bad.StatusCode -eq 400) "tenant must be a GUID: $($bad.StatusCode)"
            Api PUT '/auth/sign-in/entra' @{ enabled = $true; tenantId = $tenant; clientId = $client; createUsers = $true; clientSecret = 'zentriq-ci-value' } -Session $s | Out-Null
            Api PUT '/auth/sign-in/ldap' @{ enabled = $true; host = '127.0.0.1'; port = 636; ldaps = $true; verifyCertificate = $true; domain = 'ZQCI'; createUsers = $true } -Session $s | Out-Null
            $o = Invoke-RestMethod "$base/api/v1/auth/options"
            Assert ($o.microsoft -and $o.directory.domain -eq 'ZQCI') "options after: $($o | ConvertTo-Json -Compress)"
            $start = Limited { Invoke-WebRequest "$base/api/v1/auth/oidc/start" -MaximumRedirection 0 -SkipHttpErrorCheck -ErrorAction SilentlyContinue }
            $loc = [string]$start.Headers.Location
            Assert ($start.StatusCode -eq 302 -and $loc.StartsWith("https://login.microsoftonline.com/$tenant/oauth2/v2.0/authorize?")) "start: $($start.StatusCode) $loc"
            Assert ($loc -match 'code_challenge_method=S256' -and $loc -match "client_id=$client" -and $loc -match 'redirect_uri=https%3A%2F%2F') "authorize URL: $loc"
            $forged = Limited { Invoke-WebRequest "$base/api/v1/auth/oidc/callback?code=abc&state=forged" -SkipHttpErrorCheck }
            Assert ($forged.StatusCode -eq 200 -and (Body $forged) -match 'ssoError=') "forged callback: $($forged.StatusCode)"
            $dir = Limited { Invoke-WebRequest "$base/api/v1/auth/login" -Method Post -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body (@{ email = 'ZQCI\nobody'; password = 'not-a-password'; directory = $true } | ConvertTo-Json) -SkipHttpErrorCheck }
            Assert ($dir.StatusCode -eq 401 -and (Body $dir) -match 'domain controller') "directory sign-in without a domain controller: $($dir.StatusCode) $(Body $dir)"
            $empty = Limited { Invoke-WebRequest "$base/api/v1/auth/login" -Method Post -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body (@{ email = 'ZQCI\nobody'; password = ''; directory = $true } | ConvertTo-Json) -SkipHttpErrorCheck }
            Assert ($empty.StatusCode -eq 401) "empty directory password: $($empty.StatusCode)"
            $m = Api POST '/auth/sign-in/mappings' @{ provider = 'ldap'; groupRef = 'CN=Zentriq Admins,OU=Groups,DC=zqci,DC=local'; groupName = 'Zentriq Admins'; roleId = 'admin' } -Session $s
            $si = Api GET '/auth/sign-in' -Session $s
            Assert (@($si.mappings | Where-Object { $_.id -eq $m.id }).Count -eq 1) 'mapping not listed'
            Assert ($si.redirectUri -match '/api/v1/auth/oidc/callback$') "redirect URI $($si.redirectUri)"
            Api DELETE "/auth/sign-in/mappings/$($m.id)" -Session $s | Out-Null
            Api PUT '/auth/sign-in/entra' @{ enabled = $false; tenantId = $tenant; clientId = $client } -Session $s | Out-Null
            Api PUT '/auth/sign-in/ldap' @{ enabled = $false; host = '127.0.0.1'; port = 636; ldaps = $true; verifyCertificate = $true } -Session $s | Out-Null
            $again = Login
            $me = Api GET '/auth/me' -Session $again
            Assert ($me.email -eq $adminEmail) 'local sign-in after the directory settings'
            "authorize redirect to tenant $tenant with PKCE; forged callback refused; AD: $(((Body $dir) | ConvertFrom-Json).title); mapping added and removed; local admin signs in"
        } | Out-Null

        # ---- T92: patch management ----
        Check 'T92' 'Patch management: the agent scans with the Windows Update Agent (or says why it cannot), device patch state, policies, a deployment of updates the device does not miss, settings' {
            $s = Login
            $sum = Api GET '/patches/summary' -Session $s
            Assert ($sum.devices -ge 1) "devices with a capable agent: $($sum.devices), older agents: $($sum.olderAgents)"
            $req = Api POST '/patches/scan' @{ assetIds = @($script:agentAsset) } -Session $s
            function Last-Scan { @((Api GET "/patches/devices/$($script:agentAsset)" -Session $s).actions | Where-Object operation -eq 'scan') | Select-Object -First 1 }
            # The agent takes the scan (an automatic one may already be running beside the check-in).
            $first = Dep-Wait { $l = Last-Scan; if ($l -and $l.status -in 'running', 'succeeded', 'failed') { $l } } 300 'the agent to start the patch scan'
            # A first Windows Update scan on a fresh VM can take very long: give it 5 minutes, then make Windows Update
            # unavailable (as on devices where the service is disabled) and check that the scan ends and says why.
            $done = $null
            $deadline = (Get-Date).AddMinutes(5)
            while ((Get-Date) -lt $deadline -and -not $done) { $l = Last-Scan; if ($l.status -in 'succeeded', 'failed') { $done = $l } else { Start-Sleep 10 } }
            $wuNote = ''
            if (-not $done -or (Api GET "/patches/devices/$($script:agentAsset)" -Session $s).scan.ok -ne $true) {
                $wuStart = (Get-CimInstance Win32_Service -Filter "Name='wuauserv'").StartMode
                Stop-Service wuauserv -Force -ErrorAction SilentlyContinue
                Set-Service wuauserv -StartupType Disabled
                try {
                    try { Dep-Wait { $l = Last-Scan; if ($l.status -in 'succeeded', 'failed') { $l } } 600 'the running scan to end after Windows Update stopped' | Out-Null }
                    catch {
                        $acts = @((Api GET "/patches/devices/$($script:agentAsset)" -Session $s).actions | Where-Object operation -eq 'scan' | Select-Object -First 4 | ForEach-Object { "$($_.status) requested $($_.requestedAt) sent $($_.sentAt): $($_.detail)" }) -join ' | '
                        $wua = (Get-Content "$env:ProgramData\Zentriq\Agent\logs\agent.log" -ErrorAction SilentlyContinue | Select-String 'Patch management|scan scan|interrupted' | Select-Object -Last 6 | ForEach-Object { $_.Line }) -join ' | '
                        throw "$($_.Exception.Message); scans: $acts; agent: $wua"
                    }
                    $again = Api POST '/patches/scan' @{ assetIds = @($script:agentAsset) } -Session $s
                    Assert ($again.requested -eq 1) "second scan request: $($again | ConvertTo-Json -Compress)"
                    $done = Dep-Wait { $x = Api GET "/patches/devices/$($script:agentAsset)" -Session $s; $l = @($x.actions | Where-Object operation -eq 'scan') | Select-Object -First 1
                        if ($l.status -in 'succeeded', 'failed' -and $x.scan) { $l } } 300 'the scan with Windows Update disabled'
                    $wuNote = ' (Windows Update disabled for the check)'
                } finally { Set-Service wuauserv -StartupType $(if ($wuStart -eq 'Auto') { 'Automatic' } elseif ($wuStart -eq 'Disabled') { 'Disabled' } else { 'Manual' }) }
            }
            $dev = Api GET "/patches/devices/$($script:agentAsset)" -Session $s
            $scanText = if ($dev.scan.ok) { "$($dev.scan.missing) missing update(s) from $($dev.scan.source)$(if ($dev.scan.rebootPending) { ', restart pending' })" }
                else { Assert ($dev.scan.message -match 'Windows Update') "scan error: $($dev.scan.message)"; "scan failed as expected$($wuNote): $($dev.scan.message)" }
            $devices = @(Api GET '/patches/devices' -Session $s | ForEach-Object { $_ })
            $me = $devices | Where-Object id -eq $script:agentAsset
            Assert ($me -and $me.capable -and $me.scannedAt) "device list: $($me | ConvertTo-Json -Compress)"
            # A policy: validated, listed, deleted.
            $bad = Invoke-WebRequest "$base/api/v1/patches/policies" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' `
                -Body (@{ name = 'CI'; platform = 'windows'; classifications = @(); days = @(3); startTime = '22:00'; windowHours = 4; assetIds = @($script:agentAsset) } | ConvertTo-Json) -SkipHttpErrorCheck
            Assert ($bad.StatusCode -eq 400 -and (Body $bad) -match 'classification') "policy without classifications: $($bad.StatusCode)"
            $pol = Api POST '/patches/policies' @{ name = 'CI security updates'; platform = 'windows'; classifications = @('critical', 'security'); days = @(0, 6); startTime = '03:00'; windowHours = 2; reboot = 'never'; enabled = $false; assetIds = @($script:agentAsset) } -Session $s
            $pols = @(Api GET '/patches/policies' -Session $s | ForEach-Object { $_ })
            $p = $pols | Where-Object id -eq $pol.id
            Assert ($p -and $p.startTime -eq '03:00' -and -not $p.enabled -and @($p.targets.assetIds) -contains $script:agentAsset) "policy: $($p | ConvertTo-Json -Compress -Depth 4)"
            Api DELETE "/patches/policies/$($pol.id)" -Session $s | Out-Null
            # A deployment of an update the device does not miss: nothing is sent, and the reason is given.
            $job = Api POST '/patches/jobs' @{ patchIds = @('00000000-0000-0000-0000-00000000c1c1'); assetIds = @($script:agentAsset); reboot = 'never' } -Session $s
            Assert ($job.sent -eq 0 -and $job.skipped -eq 1 -and $job.nothing -eq 1) "job: $($job | ConvertTo-Json -Compress)"
            $jd = @(Api GET "/patches/jobs/$($job.id)" -Session $s | ForEach-Object { $_ }) | Select-Object -First 1
            Assert ($jd.status -eq 'skipped' -and $jd.detail -match 'Nothing to install') "job device: $($jd | ConvertTo-Json -Compress)"
            # Settings are validated.
            $set = Invoke-WebRequest "$base/api/v1/patches/settings" -Method Put -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body '{"autoScan":true,"scanHours":0,"rebootDelayMinutes":10,"rebootMessage":"x"}' -SkipHttpErrorCheck
            Assert ($set.StatusCode -eq 400) "scan every 0 hours: $($set.StatusCode)"
            $act = Api GET '/activity?kind=patch' -Session $s
            Assert ($act.total -ge 1 -and $null -eq @($act.items)[0].argument) "activity log: $($act.total) patch action(s)"
            "$scanText; policy saved and deleted; deployment of an update the runner does not miss skipped; $($act.total) patch action(s) in the activity log"
        } | Out-Null

        # ---- T93: scripts and remediation ----
        Check 'T93' 'Scripts: a PowerShell script runs as SYSTEM with its parameters, output and exit code; a remediation fixes and then reports compliant; a time limit stops a script; scripts can be turned off' {
            $s = Login
            $lib = @(Api GET '/scripts' -Session $s | ForEach-Object { $_ })
            Assert (@($lib | Where-Object builtIn).Count -ge 10) "ready-made scripts: $(@($lib | Where-Object builtIn).Count)"
            $marker = Join-Path $env:ProgramData 'zq-ci-script.txt'
            $fixed = Join-Path $env:ProgramData 'zq-ci-fixed.txt'
            Remove-Item $marker, $fixed -ErrorAction SilentlyContinue
            function Wait-Run([string] $Id, [string] $What) {
                Dep-Wait { $x = @(Api GET "/scripts/runs/$Id" -Session $s | ForEach-Object { $_ }) | Select-Object -First 1; if ($x.status -in 'succeeded', 'failed', 'expired') { $x } } 240 $What
            }
            $hello = Api POST '/scripts' @{ name = 'CI Hello'; platform = 'windows'; language = 'powershell'; parameters = '-Name ci'; timeoutSeconds = 120
                content = "param([string]`$Name)`n`"hello `$Name from `$([Security.Principal.WindowsIdentity]::GetCurrent().Name)`"`nSet-Content -LiteralPath '$marker' -Value 'ran'`nWrite-Error 'a warning on stderr'`nexit 3" } -Session $s
            $run = Api POST '/scripts/runs' @{ scriptId = $hello.id; assetIds = @($script:agentAsset) } -Session $s
            Assert ($run.sent -eq 1) "run: $($run | ConvertTo-Json -Compress)"
            $d = Wait-Run $run.id 'the script on the runner'
            Assert ($d.status -eq 'failed' -and $d.exitCode -eq 3 -and $d.outcome -eq 'failed') "result: $($d.status) exit $($d.exitCode) $($d.detail)"
            Assert ($d.stdout -match 'hello ci from NT AUTHORITY\\SYSTEM') "stdout: $($d.stdout)"
            Assert ($d.stderr -match 'a warning on stderr') "stderr: $($d.stderr)"
            Assert ((Test-Path $marker) -and (Get-Content $marker -Raw) -match 'ran') 'the script did not run on the device'
            # A ready-made script.
            $disk = $lib | Where-Object name -eq 'Disk Space Report'
            $r2 = Api POST '/scripts/runs' @{ scriptId = $disk.id; assetIds = @($script:agentAsset) } -Session $s
            $d2 = Wait-Run $r2.id 'the ready-made script'
            Assert ($d2.status -eq 'succeeded' -and $d2.exitCode -eq 0 -and $d2.stdout -match 'GB free') "disk space: $($d2.status) $($d2.stdout)"
            # A remediation: the detection finds the problem, the fix runs, the check after it passes; the next run is compliant.
            $det = Api POST '/scripts' @{ name = 'CI Detect'; platform = 'windows'; language = 'powershell'; timeoutSeconds = 60; content = "if (Test-Path -LiteralPath '$fixed') { 'fine'; exit 0 }`n'missing'; exit 1" } -Session $s
            $fix = Api POST '/scripts' @{ name = 'CI Fix'; platform = 'windows'; language = 'cmd'; timeoutSeconds = 60; content = "@echo off`r`necho fixed> `"$fixed`"`r`necho done" } -Session $s
            $rem = Api POST '/scripts/runs' @{ scriptId = $fix.id; detectScriptId = $det.id; assetIds = @($script:agentAsset) } -Session $s
            $d3 = Wait-Run $rem.id 'the remediation'
            Assert ($d3.status -eq 'succeeded' -and $d3.outcome -eq 'remediated' -and (Test-Path $fixed)) "remediation: $($d3.outcome) $($d3.detail) $($d3.stdout)"
            $rem2 = Api POST '/scripts/runs' @{ scriptId = $fix.id; detectScriptId = $det.id; assetIds = @($script:agentAsset) } -Session $s
            $d4 = Wait-Run $rem2.id 'the second remediation'
            Assert ($d4.outcome -eq 'compliant') "second remediation: $($d4.outcome) $($d4.detail)"
            # A time limit.
            $slow = Api POST '/scripts' @{ name = 'CI Slow'; platform = 'windows'; language = 'powershell'; timeoutSeconds = 10; content = 'Start-Sleep -Seconds 120' } -Session $s
            $r5 = Api POST '/scripts/runs' @{ scriptId = $slow.id; assetIds = @($script:agentAsset) } -Session $s
            $d5 = Wait-Run $r5.id 'the script with a time limit'
            Assert ($d5.status -eq 'failed' -and $d5.outcome -eq 'timeout' -and $d5.detail -match 'did not finish') "time limit: $($d5.outcome) $($d5.detail)"
            # Turned off for the organisation: nothing can be started.
            Api PUT '/scripts/settings' @{ enabled = $false; keepOutputDays = 90 } -Session $s | Out-Null
            $off = Invoke-WebRequest "$base/api/v1/scripts/runs" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body (@{ scriptId = $hello.id; assetIds = @($script:agentAsset) } | ConvertTo-Json) -SkipHttpErrorCheck
            Assert ($off.StatusCode -eq 400 -and (Body $off) -match 'turned off') "run while turned off: $($off.StatusCode)"
            Api PUT '/scripts/settings' @{ enabled = $true; keepOutputDays = 90 } -Session $s | Out-Null
            # Ready-made scripts cannot be changed or deleted.
            $ro = Invoke-WebRequest "$base/api/v1/scripts/$($disk.id)" -Method Delete -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -SkipHttpErrorCheck
            Assert ($ro.StatusCode -eq 404) "delete a ready-made script: $($ro.StatusCode)"
            foreach ($x in $hello, $det, $fix, $slow) { Api DELETE "/scripts/$($x.id)" -Session $s | Out-Null }
            Remove-Item $marker, $fixed -ErrorAction SilentlyContinue
            "ran as $((($d.stdout -split "`n") | Where-Object { $_ -match 'hello' } | Select-Object -First 1).Trim()) (exit 3, stderr kept); disk report: $(($d2.stdout -split "`n")[0].Trim()); remediation: remediated then compliant; time limit stopped the script after $([math]::Round($d5.durationMs / 1000)) s"
        } | Out-Null

        # ---- T94: tray icon, Software Center and Get Remote Help ----
        Check 'T94' 'Tray icon: started on the user''s desktop; Software Center installs offered software without administrator rights (and after IT approval); Get Remote Help reaches Remote Support; device information' {
            $s = Login
            function Tray-Call($Req) {
                $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ZentriqAgentTray', [System.IO.Pipes.PipeDirection]::InOut)
                $p.Connect(5000)
                $w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true
                $r = New-Object System.IO.StreamReader($p)
                $w.WriteLine(($Req | ConvertTo-Json -Compress))
                $line = $r.ReadLine(); $p.Dispose()
                $o = $line | ConvertFrom-Json
                if (-not $o.ok) { throw "tray: $($o.error)" }
                return $o.data
            }
            function Shot([string] $Name) {
                try {
                    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
                    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
                    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
                    $g = [System.Drawing.Graphics]::FromImage($bmp)
                    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                    $bmp.Save((Join-Path $OutDir "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
                    $g.Dispose(); $bmp.Dispose()
                } catch { Write-Host "screenshot $Name not possible: $($_.Exception.Message)" }
            }
            Api PUT '/deployment/self-service/settings' @{ enabled = $true; softwareCenter = $true; remoteHelp = $true; companyName = 'Contoso IT'; supportText = 'Urgent? Call the service desk on 4711.' } -Session $s | Out-Null
            $tool = Join-Path $OutDir 'zq-ss-tool-setup.exe'
            Copy-Item (Join-Path $env:windir 'System32\cmd.exe') $tool -Force
            $marker1 = Join-Path $env:ProgramData 'zq-ss-1.txt'; $marker2 = Join-Path $env:ProgramData 'zq-ss-2.txt'
            Remove-Item $marker1, $marker2 -ErrorAction SilentlyContinue
            $p1 = Api POST '/deployment/packages' @{ uploadId = (Dep-Upload $s $tool).uploadId; name = 'ZQ Notepad Plus'; version = '8.6'; publisher = 'CI Publisher'; installArgs = "/c echo ok> `"$marker1`"" } -Session $s
            $p2 = Api POST '/deployment/packages' @{ uploadId = (Dep-Upload $s $tool).uploadId; name = 'ZQ Visio Viewer'; version = '2024'; publisher = 'CI Publisher'; installArgs = "/c echo ok> `"$marker2`"" } -Session $s
            Api PUT "/deployment/self-service/packages/$($p1.id)" @{ enabled = $true; approval = $false; description = 'Text editor for code and notes.'; category = 'Productivity' } -Session $s | Out-Null
            Api PUT "/deployment/self-service/packages/$($p2.id)" @{ enabled = $true; approval = $true; description = 'Opens Visio drawings. Needs approval by IT.'; category = 'Office' } -Session $s | Out-Null
            # The tray on the signed-in user's desktop.
            $tray = Dep-Wait { Get-CimInstance Win32_Process -Filter "Name='ZentriqAgent.exe'" | Where-Object CommandLine -match '--tray\b' | Select-Object -First 1 } 120 'the tray icon on the desktop'
            $owner = Invoke-CimMethod -InputObject $tray -MethodName GetOwner
            $info = Tray-Call @{ op = 'info' }
            Assert ($info.computer -eq $env:COMPUTERNAME -and $info.agentVersion -and $info.user) "device information: $($info | ConvertTo-Json -Compress)"
            # Software Center: offered, installed by the agent, then shown as installed.
            $cat = @(Tray-Call @{ op = 'catalog' })
            $c1 = $cat | Where-Object id -eq $p1.id; $c2 = $cat | Where-Object id -eq $p2.id
            Assert ($c1 -and $c2 -and $c1.state -eq 'available' -and $c2.approval) "catalog: $($cat | ConvertTo-Json -Compress -Depth 4)"
            $i1 = Tray-Call @{ op = 'install'; packageId = $p1.id }
            Assert ($i1.state -eq 'installing') "install: $($i1 | ConvertTo-Json -Compress)"
            Dep-Wait { $x = @(Tray-Call @{ op = 'catalog' }) | Where-Object id -eq $p1.id; if ($x.state -in 'installed', 'failed') { $x } } 180 'the Software Center installation' | ForEach-Object { Assert ($_.state -eq 'installed') "state: $($_.state) $($_.message)" }
            Assert (Test-Path $marker1) 'the self-service package did not run on the device'
            # Needs approval: a request, approved in the console, then installed.
            $i2 = Tray-Call @{ op = 'install'; packageId = $p2.id; reason = 'CI: open the network drawing' }
            Assert ($i2.state -eq 'pending') "request: $($i2 | ConvertTo-Json -Compress)"
            $req = (Api GET '/deployment/self-service' -Session $s).requests | Where-Object { $_.packageId -eq $p2.id -and $_.status -eq 'pending' } | Select-Object -First 1
            Assert ($req -and $req.reason -like 'CI:*' -and $req.user) "request in the console: $($req | ConvertTo-Json -Compress)"
            Api POST "/deployment/self-service/requests/$($req.id)" @{ approve = $true; note = 'ok' } -Session $s | Out-Null
            Dep-Wait { $x = @(Tray-Call @{ op = 'catalog' }) | Where-Object id -eq $p2.id; if ($x.state -in 'installed', 'failed') { $x } } 180 'the approved installation' | ForEach-Object { Assert ($_.state -eq 'installed') "state: $($_.state) $($_.message)" }
            Assert (Test-Path $marker2) 'the approved package did not run on the device'
            # Get Remote Help: a request in Remote Support, closed by the technician.
            $h = Tray-Call @{ op = 'help'; message = 'CI: Outlook does not start'; contact = 'ext. 4711' }
            Assert ($h.number -gt 0) "help: $($h | ConvertTo-Json -Compress)"
            $hr = @(Api GET '/remote/help-requests' -Session $s | ForEach-Object { $_ }) | Where-Object number -eq $h.number
            Assert ($hr -and $hr.device -and $hr.contact -eq 'ext. 4711' -and $hr.user) "help request in the console: $($hr | ConvertTo-Json -Compress)"
            Api POST "/remote/help-requests/$($hr.id)" @{ status = 'closed'; note = 'CI done' } -Session $s | Out-Null
            $mine = @(Tray-Call @{ op = 'helpList' })
            Assert (@($mine | Where-Object { $_.number -eq $h.number -and $_.status -eq 'closed' }).Count -eq 1) "the user's requests: $($mine | ConvertTo-Json -Compress)"
            # The windows, for the record.
            $agentExe = Join-Path $env:ProgramFiles 'Zentriq Agent\ZentriqAgent.exe'
            foreach ($w in 'store', 'help', 'info') {
                $pw = Start-Process $agentExe -ArgumentList '--tray-window', $w -PassThru
                Start-Sleep 7
                Shot "tray-$w"
                Stop-Process -Id $pw.Id -Force -ErrorAction SilentlyContinue
            }
            Remove-Item $marker1, $marker2 -ErrorAction SilentlyContinue
            "tray running for $($owner.Domain)\$($owner.User); Software Center: installed without approval and after approval; help request #$($h.number) in Remote Support and closed; device $($info.computer), agent $($info.agentVersion)"
        } | Out-Null

        # ---- T95: software metering and Device Experience ----
        Check 'T95' 'Software metering and Device Experience: programs run by the signed-in user are metered, a licensed product shows its installation as used, the device gets an experience score; metering can be turned off' {
            $s = Login
            # A program outside the Windows folder, running on the signed-in user's desktop (this session).
            $app = Join-Path $OutDir 'zq-meter-app.exe'
            Copy-Item (Join-Path $env:windir 'System32\cmd.exe') $app -Force
            $proc = Start-Process $app -ArgumentList '/k', 'title Zentriq metering test' -PassThru
            try {
                # Two samples (one a minute), then a fresh inventory.
                Start-Sleep 150
                Api POST '/assets/collect' @{ assetIds = @($script:agentAsset) } -Session $s | Out-Null
                $use = Dep-Wait { $u = Api GET "/sam/usage/device/$($script:agentAsset)" -Session $s; $x = @($u.programs) | Where-Object exe -eq 'zq-meter-app.exe'; if ($x -and $x.minutes -ge 1) { $u } } 240 'the metered program in the console'
                $prog = @($use.programs) | Where-Object exe -eq 'zq-meter-app.exe'
                Assert ($use.meteredSince -and $prog.users -and $prog.daysUsed -eq 1) "device usage: $($use | ConvertTo-Json -Compress -Depth 4)"
                Assert (-not (@($use.programs) | Where-Object { $_.exe -in 'svchost.exe', 'explorer.exe', 'ZentriqAgent.exe' })) 'Windows or agent programs were metered'
                # A licensed product with that program: its installation on this device is used.
                $installed = (Api GET "/assets/$($script:agentAsset)/inventory" -Session $s).software | Where-Object { $_.name -and $_.name.Length -ge 4 } | Select-Object -First 1
                $prod = Api POST '/sam/products' @{ name = 'CI Metered Product'; kind = 'installed'; metric = 'device'; namePatterns = @($installed.name); exePatterns = @('zq-meter-app.exe') } -Session $s
                $inst = @(Api GET "/sam/products/$($prod.id)/installations" -Session $s | ForEach-Object { $_ }) | Where-Object assetId -eq $script:agentAsset
                Assert ($inst -and $inst.usage -eq 'used' -and $inst.lastUsed) "installation: $($inst | ConvertTo-Json -Compress)"
                $sum = Api GET '/sam/usage?days=7' -Session $s
                $row = @($sum.apps) | Where-Object exe -eq 'zq-meter-app.exe'
                Assert ($row -and $row.productId -eq $prod.id -and $sum.meteredDevices -ge 1) "usage summary: $($row | ConvertTo-Json -Compress)"
                $p1 = @($sum.products) | Where-Object id -eq $prod.id
                Assert ($p1.used -eq 1 -and $p1.unused -eq 0) "product usage: $($p1 | ConvertTo-Json -Compress)"
                # Device Experience: a score with what the agent reported.
                $exp = Dep-Wait { $e = Api GET "/experience/devices/$($script:agentAsset)" -Session $s; if ($e.current) { $e } } 60 'the experience score'
                Assert ($exp.current.score -ge 0 -and $exp.current.score -le 100 -and $exp.current.rating -in 'good', 'fair', 'poor' -and $exp.current.systemFree -gt 0) "experience: $($exp.current | ConvertTo-Json -Compress -Depth 4)"
                Assert (@($exp.days).Count -ge 1 -and $exp.days[0].samples -ge 2) "resource samples: $($exp.days | ConvertTo-Json -Compress)"
                $fleet = Api GET '/experience/summary' -Session $s
                Assert ($fleet.devices -ge 1 -and $fleet.score -ge 0) "fleet: $($fleet | ConvertTo-Json -Compress -Depth 2)"
                $list = @(Api GET '/experience/devices' -Session $s | ForEach-Object { $_ }) | Where-Object assetId -eq $script:agentAsset
                Assert $list 'the device is not in the Device Experience list'
                # Metering off: the agent stops metering and forgets its totals; on again.
                $set = Api GET '/sam/settings' -Session $s
                Api PUT '/sam/settings' @{ metering = $false } -Session $s | Out-Null
                Api POST '/assets/collect' @{ assetIds = @($script:agentAsset) } -Session $s | Out-Null
                function Reg-Metering { $v = Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent' -ErrorAction SilentlyContinue; if ($v -and ($v.PSObject.Properties.Name -contains 'Metering')) { [int]$v.Metering } else { -1 } }
                Dep-Wait { (Reg-Metering) -eq 0 } 120 'metering turned off on the device' | Out-Null
                Api PUT '/sam/settings' @{ metering = $true } -Session $s | Out-Null
                Api POST '/assets/collect' @{ assetIds = @($script:agentAsset) } -Session $s | Out-Null
                Dep-Wait { (Reg-Metering) -eq 1 } 120 'metering turned on again' | Out-Null
                Api DELETE "/sam/products/$($prod.id)" -Session $s | Out-Null
                $boot = @($exp.events) | Where-Object kind -eq 'boot' | Select-Object -First 1
                "metered zq-meter-app.exe: $($prog.minutes) min, user $(@($prog.users)[0]); product installation used; score $($exp.current.score) ($($exp.current.rating)), $($exp.days[0].samples) samples, $(@($exp.events).Count) event(s)$(if ($boot) { ", start $([math]::Round($boot.ms / 1000)) s" }); metering off and on again on the device"
            } finally { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Remove-Item $app -ErrorAction SilentlyContinue }
        } | Out-Null

        # Information only (no result): what this Windows offers for the Security Pack of the next build.
        $probe = [System.Collections.Generic.List[string]]::new()
        function Probe([string] $Name, [scriptblock] $Do) { try { $probe.Add("${Name}: $(& $Do)") } catch { $probe.Add("${Name}: error $($_.Exception.Message)") } }
        Probe 'os' { "$((Get-CimInstance Win32_OperatingSystem).Caption) $([Environment]::OSVersion.Version)" }
        Probe 'manage-bde' { Test-Path (Join-Path $env:windir 'System32\manage-bde.exe') }
        Probe 'Win32_EncryptableVolume' { $v = @(Get-CimInstance -Namespace 'root\cimv2\Security\MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume -ErrorAction Stop); "$($v.Count) volume(s): $(($v | ForEach-Object { "$($_.DriveLetter) type=$($_.VolumeType) protection=$($_.ProtectionStatus)" }) -join '; ')" }
        Probe 'feature BitLocker' { (Get-WindowsFeature BitLocker -ErrorAction Stop).InstallState }
        Probe 'TPM' { $t = Get-CimInstance -Namespace 'root\cimv2\Security\MicrosoftTpm' -ClassName Win32_Tpm -ErrorAction Stop; if ($t) { "enabled=$($t.IsEnabled_InitialValue) version=$($t.SpecVersion)" } else { 'none' } }
        Probe 'pnputil disable-device' { (& pnputil.exe /? 2>&1 | Out-String) -match 'disable-device' }
        Probe 'cmdlets' { "Set-Disk $([bool](Get-Command Set-Disk -ErrorAction SilentlyContinue)), New-VHD $([bool](Get-Command New-VHD -ErrorAction SilentlyContinue)), Get-PnpDevice $([bool](Get-Command Get-PnpDevice -ErrorAction SilentlyContinue))" }
        $vhd = Join-Path $OutDir 'zq-probe.vhdx'
        Probe 'vdisk' {
            "create vdisk file=`"$vhd`" maximum=512 type=expandable`r`nattach vdisk`r`ncreate partition primary`r`nformat fs=ntfs quick label=ZQPROBE`r`nassign`r`n" | Set-Content (Join-Path $OutDir 'zq-probe.dp') -Encoding ascii
            $dp = & diskpart.exe /s (Join-Path $OutDir 'zq-probe.dp') 2>&1 | Out-String
            $vol = Get-Volume -FileSystemLabel ZQPROBE -ErrorAction SilentlyContinue
            if (-not $vol) { return 'not created: ' + ($dp -replace '\s+', ' ') }
            $disk = Get-Partition -DriveLetter $vol.DriveLetter | Get-Disk
            $wmi = Get-CimInstance Win32_DiskDrive | Where-Object Index -eq $disk.Number
            "drive $($vol.DriveLetter): bus $($disk.BusType), model '$($wmi.Model)', interface $($wmi.InterfaceType), pnp $($wmi.PNPDeviceID), number $($disk.Number)"
        }
        Probe 'vdisk BitLocker' { $vol = Get-Volume -FileSystemLabel ZQPROBE -ErrorAction Stop; ((& manage-bde.exe -status "$($vol.DriveLetter):" 2>&1 | Out-String) -replace '\s+', ' ').Trim() }
        Probe 'vdisk BitLocker on' { $vol = Get-Volume -FileSystemLabel ZQPROBE -ErrorAction Stop; ((& manage-bde.exe -on "$($vol.DriveLetter):" -RecoveryPassword -UsedSpaceOnly 2>&1 | Out-String) -replace '\s+', ' ').Trim() }
        Probe 'vdisk read-only' {
            $vol = Get-Volume -FileSystemLabel ZQPROBE -ErrorAction Stop; $n = (Get-Partition -DriveLetter $vol.DriveLetter | Get-Disk).Number
            "select disk $n`r`nattributes disk set readonly`r`noffline disk`r`nonline disk`r`n" | Set-Content (Join-Path $OutDir 'zq-probe3.dp') -Encoding ascii
            $o = & diskpart.exe /s (Join-Path $OutDir 'zq-probe3.dp') 2>&1 | Out-String
            $w = try { 'x' | Set-Content "$($vol.DriveLetter):\w.txt" -ErrorAction Stop; 'writable' } catch { 'write refused' }
            "$w; $(($o -replace '\s+', ' ').Trim())"
        }
        Probe 'vdisk disable' {
            $vol = Get-Volume -FileSystemLabel ZQPROBE -ErrorAction Stop; $n = (Get-Partition -DriveLetter $vol.DriveLetter | Get-Disk).Number
            $pnp = (Get-CimInstance Win32_DiskDrive | Where-Object Index -eq $n).PNPDeviceID
            $o = & pnputil.exe /disable-device "$pnp" 2>&1 | Out-String
            $gone = -not (Get-Volume -FileSystemLabel ZQPROBE -ErrorAction SilentlyContinue)
            $e = & pnputil.exe /enable-device "$pnp" 2>&1 | Out-String
            "exit $LASTEXITCODE gone=$gone; $(($o -replace '\s+', ' ').Trim()) / $(($e -replace '\s+', ' ').Trim())"
        }
        try { "select vdisk file=`"$vhd`"`r`ndetach vdisk`r`n" | Set-Content (Join-Path $OutDir 'zq-probe2.dp') -Encoding ascii; & diskpart.exe /s (Join-Path $OutDir 'zq-probe2.dp') 2>&1 | Out-Null } catch { }
        Remove-Item $vhd, (Join-Path $OutDir 'zq-probe*.dp') -ErrorAction SilentlyContinue
        $probe | Set-Content (Join-Path $OutDir 'security-pack-probe.txt')

        # ---- Security Pack (build 122) ----
        function Pipe-Call($Req) {
            $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ZentriqAgentTray', [System.IO.Pipes.PipeDirection]::InOut)
            $p.Connect(5000)
            $w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true
            $r = New-Object System.IO.StreamReader($p)
            $w.WriteLine(($Req | ConvertTo-Json -Compress))
            $line = $r.ReadLine(); $p.Dispose()
            $o = $line | ConvertFrom-Json
            if (-not $o.ok) { throw "agent: $($o.error)" }
            return $o.data
        }
        function Admin-Members { @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) }
        function Wake-Agent($s) { Api POST '/assets/collect' @{ assetIds = @($script:agentAsset) } -Session $s | Out-Null }

        Check 'T96' 'Endpoint privilege management: extra administrators removed in enforce mode (allowed ones kept), administrator rights for a while given and ended, a request approved, an approved application started as administrator, a not approved one refused' {
            $s = Login
            $pw = 'Zq-' + [guid]::NewGuid().ToString('N').Substring(0, 16) + '!a1'
            $sec = ConvertTo-SecureString $pw -AsPlainText -Force
            foreach ($u in 'zqextra', 'zqstd') { Remove-LocalUser -Name $u -ErrorAction SilentlyContinue; New-LocalUser -Name $u -Password $sec -AccountNeverExpires | Out-Null }
            Add-LocalGroupMember -SID 'S-1-5-32-544' -Member 'zqextra'
            # Everyone who is an administrator now (the runner's own accounts) stays; zqextra must go.
            $keep = @(Admin-Members | Where-Object { $_ -notmatch '\\zqextra$' })
            try {
                Api PUT '/security-pack/privilege/policy' @{ mode = 'enforce'; allowedAdmins = $keep; elevation = $true; requests = $true; maxAdminMinutes = 240 } -Session $s | Out-Null
                Wake-Agent $s
                Dep-Wait { -not (Admin-Members | Where-Object { $_ -match '\\zqextra$' }) } 180 'zqextra removed from Administrators' | Out-Null
                foreach ($k in $keep) { Assert ((Admin-Members) -contains $k) "$k was removed although allowed" }
                $ev = Dep-Wait { @(Api GET "/security-pack/privilege/events?kind=member_removed&assetId=$($script:agentAsset)" -Session $s | ForEach-Object { $_ }) | Where-Object { $_.user -match 'zqextra' } } 120 'the removal in the console'
                $adm = @(Api GET '/security-pack/privilege/admins' -Session $s | ForEach-Object { $_ }) | Where-Object assetId -eq $script:agentAsset
                Assert ($adm.Count -ge 1 -and -not ($adm | Where-Object { $_.status -eq 'to_remove' })) "administrators in the console: $($adm | ConvertTo-Json -Compress)"

                # Administrator rights for a while, given in the console and ended early.
                $g = Api POST '/security-pack/privilege/grants' @{ assetId = $script:agentAsset; user = "$env:COMPUTERNAME\zqstd"; minutes = 15; reason = 'CI' } -Session $s
                Wake-Agent $s
                Dep-Wait { (Admin-Members) -match '\\zqstd$' } 180 'zqstd made administrator' | Out-Null
                Api DELETE "/security-pack/privilege/grants/$($g.id)" -Session $s | Out-Null
                Wake-Agent $s
                Dep-Wait { -not ((Admin-Members) -match '\\zqstd$') } 180 'the rights of zqstd ended' | Out-Null

                # A request from the signed-in user (tray), approved in the console.
                $req = Pipe-Call @{ op = 'requestAdmin'; minutes = '30'; reason = 'CI: install a printer driver' }
                $pending = @(Api GET '/security-pack/privilege/requests?status=pending' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $req.id
                Assert ($pending -and $pending.kind -eq 'admin' -and $pending.minutes -eq 30 -and $pending.user) "request in the console: $($pending | ConvertTo-Json -Compress)"
                $dec = Api POST "/security-pack/privilege/requests/$($req.id)" @{ approve = $true; minutes = 15 } -Session $s
                Assert ($dec.decision -match '15 minutes') "decision: $($dec | ConvertTo-Json -Compress)"
                $grant = @(Api GET '/security-pack/privilege/grants' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.active -and $_.user -eq $pending.user } | Select-Object -First 1
                Assert $grant 'no active grant after approval'
                Api DELETE "/security-pack/privilege/grants/$($grant.id)" -Session $s | Out-Null

                # Run as administrator: an approved program (by hash) starts elevated on the desktop; another is refused.
                $ok = Join-Path $OutDir 'zq-elev.exe'; Copy-Item (Join-Path $env:windir 'System32\cmd.exe') $ok -Force
                $no = Join-Path $OutDir 'zq-noelev.exe'; Copy-Item (Join-Path $env:windir 'System32\whoami.exe') $no -Force
                $out = Join-Path $OutDir 'zq-elev.txt'; Remove-Item $out -ErrorAction SilentlyContinue
                $hash = (Get-FileHash $ok -Algorithm SHA256).Hash.ToLowerInvariant()
                $rule = Api POST '/security-pack/privilege/rules' @{ name = 'CI elevated shell'; kind = 'hash'; value = $hash } -Session $s
                Wake-Agent $s
                $res = Dep-Wait { $r = Pipe-Call @{ op = 'elevate'; path = $ok; arguments = "/c whoami /groups /fo csv > `"$out`"" }; if ($r.state -ne 'not_approved') { $r } } 120 'the approved program'
                Assert ($res.state -eq 'started') "elevate: $($res | ConvertTo-Json -Compress)"
                Dep-Wait { (Test-Path $out) -and (Get-Item $out).Length -gt 0 } 60 'the elevated program''s output' | Out-Null
                $groups = Get-Content $out -Raw
                Assert ($groups -match 'High Mandatory Level' -and $groups -match 'S-1-5-32-544') "elevated groups: $($groups.Substring(0, [Math]::Min(600, $groups.Length)))"
                $ref = Pipe-Call @{ op = 'elevate'; path = $no }
                Assert ($ref.state -eq 'not_approved' -and $ref.sha256) "not approved: $($ref | ConvertTo-Json -Compress)"
                Dep-Wait { @(Api GET "/security-pack/privilege/events?assetId=$($script:agentAsset)" -Session $s | ForEach-Object { $_ }) | Where-Object { $_.kind -eq 'elevated' -and $_.path -like '*zq-elev.exe' } } 120 'the elevation in the console' | Out-Null
                Assert (Test-Path 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\exefile\shell\ZentriqElevate\command') 'Explorer command missing'
                Api DELETE "/security-pack/privilege/rules/$($rule.id)" -Session $s | Out-Null
                "zqextra removed ($($keep.Count) allowed administrator(s) kept); rights for a while given and ended; request approved for 15 min; zq-elev.exe started at High integrity with Administrators, zq-noelev.exe refused; Explorer command present"
            } finally {
                try { Api PUT '/security-pack/privilege/policy' @{ mode = 'off'; allowedAdmins = @(); elevation = $false; requests = $false } -Session $s | Out-Null; Wake-Agent $s } catch { }
                foreach ($u in 'zqextra', 'zqstd') { Remove-LocalUser -Name $u -ErrorAction SilentlyContinue }
            }
        } | Out-Null

        # A virtual disk for the BitLocker and USB tests: attached, formatted, with a drive letter.
        function New-TestDisk([string] $Label) {
            $file = Join-Path $OutDir "$Label.vhdx"
            Remove-Item $file -ErrorAction SilentlyContinue
            "create vdisk file=`"$file`" maximum=512 type=expandable`r`nattach vdisk`r`ncreate partition primary`r`nformat fs=ntfs quick label=$Label`r`nassign`r`n" | Set-Content (Join-Path $OutDir "$Label.dp") -Encoding ascii
            & diskpart.exe /s (Join-Path $OutDir "$Label.dp") 2>&1 | Out-Null
            $vol = Dep-Wait { Get-Volume -FileSystemLabel $Label -ErrorAction SilentlyContinue } 60 "the virtual disk $Label"
            $num = (Get-Partition -DriveLetter $vol.DriveLetter | Get-Disk).Number
            [pscustomobject]@{ File = $file; Letter = "$($vol.DriveLetter):"; Number = $num; Pnp = (Get-CimInstance Win32_DiskDrive | Where-Object Index -eq $num).PNPDeviceID; Model = (Get-CimInstance Win32_DiskDrive | Where-Object Index -eq $num).Model }
        }
        function Remove-TestDisk($d) {
            if (-not $d) { return }
            try { & pnputil.exe /enable-device "$($d.Pnp)" 2>&1 | Out-Null } catch { }
            "select vdisk file=`"$($d.File)`"`r`ndetach vdisk`r`n" | Set-Content (Join-Path $OutDir 'detach.dp') -Encoding ascii
            & diskpart.exe /s (Join-Path $OutDir 'detach.dp') 2>&1 | Out-Null
            Remove-Item $d.File -ErrorAction SilentlyContinue
        }

        $bitlockerHere = $null -ne (Get-CimInstance -Namespace 'root\cimv2\Security\MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume -ErrorAction SilentlyContinue | Select-Object -First 1)
        if (-not $bitlockerHere) {
            Record 'T97' 'BitLocker management' 'NOT RUN' 'BitLocker is not installed on this Windows Server (adding the feature needs a restart); escrow, lookup and replacement are covered by the PostgreSQL test'
        } else {
            Check 'T97' 'BitLocker management: status reported, a recovery password kept encrypted on the server and shown with a reason (audited), replaced on request and the old one retired; enforce explains a missing TPM' {
                $s = Login
                $d = New-TestDisk 'ZQBDE'
                try {
                    $on = & manage-bde.exe -on $d.Letter -RecoveryPassword -UsedSpaceOnly 2>&1 | Out-String
                    $prot = & manage-bde.exe -protectors -get $d.Letter -Type RecoveryPassword 2>&1 | Out-String
                    $m = [regex]::Match($prot, '\{([0-9A-F-]{36})\}[\s\S]*?(\d{6}(?:-\d{6}){7})')
                    Assert $m.Success "manage-bde: $on $prot"
                    $keyId = $m.Groups[1].Value.ToUpperInvariant(); $password = $m.Groups[2].Value
                    Api PUT '/security-pack/bitlocker/policy' @{ mode = 'report'; rotateAfterView = $true } -Session $s | Out-Null
                    Wake-Agent $s
                    $vol = Dep-Wait { @(Api GET '/security-pack/bitlocker/volumes?filter=' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.assetId -eq $script:agentAsset -and $_.volume -eq $d.Letter -and $_.keys -ge 1 } } 180 'the BitLocker drive and its key in the console'
                    Assert ($vol.kind -eq 'fixed' -and $vol.protectors -contains 'Recovery password') "volume: $($vol | ConvertTo-Json -Compress)"
                    $key = @(Api GET "/security-pack/bitlocker/keys?q=$($keyId.Substring(0, 8))" -Session $s | ForEach-Object { $_ }) | Where-Object keyId -eq $keyId
                    Assert $key "key $keyId not found by its first characters"
                    $bad = Invoke-WebRequest "$base/api/v1/security-pack/bitlocker/keys/$($key.id)/reveal" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body '{"reason":""}' -SkipHttpErrorCheck
                    Assert ($bad.StatusCode -eq 400) "reveal without a reason: $($bad.StatusCode)"
                    $shown = Api POST "/security-pack/bitlocker/keys/$($key.id)/reveal" @{ reason = 'CI: ticket 4711, locked laptop' } -Session $s
                    Assert ($shown.password -eq $password -and $shown.rotates) "revealed password differs: $($shown.keyId)"
                    $aud = Api GET '/audit?action=bitlocker.key_view&pageSize=5' -Session $s
                    Assert (@($aud.items | Where-Object { $_.target -match $keyId }).Count -ge 1) 'the view is not in the audit log'
                    # Shown: the device replaces it and the old one is retired.
                    Wake-Agent $s
                    $new = Dep-Wait { $p = & manage-bde.exe -protectors -get $d.Letter -Type RecoveryPassword 2>&1 | Out-String; if ($p -notmatch $keyId -and $p -match '\{([0-9A-F-]{36})\}') { $Matches[1].ToUpperInvariant() } } 180 'the recovery password replaced on the device'
                    Dep-Wait { $k = @(Api GET "/security-pack/bitlocker/keys?assetId=$($script:agentAsset)" -Session $s | ForEach-Object { $_ }); ($k | Where-Object { $_.keyId -eq $new -and -not $_.retiredAt }) -and ($k | Where-Object { $_.keyId -eq $keyId -and $_.retiredAt }) } 180 'the new key kept and the old one retired' | Out-Null
                    # Enforce on the system drive without a TPM: not encrypted, with the reason (a runner with a TPM is not encrypted by the test).
                    $tpm = $null -ne (Get-CimInstance -Namespace 'root\cimv2\Security\MicrosoftTpm' -ClassName Win32_Tpm -ErrorAction SilentlyContinue | Select-Object -First 1)
                    $enforce = 'not checked (the runner has a TPM)'
                    if (-not $tpm) {
                        Api PUT '/security-pack/bitlocker/policy' @{ mode = 'enforce'; method = 'XtsAes128'; usedSpaceOnly = $true } -Session $s | Out-Null
                        Wake-Agent $s
                        $os = Dep-Wait { @(Api GET '/security-pack/bitlocker/volumes?filter=os' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.assetId -eq $script:agentAsset -and $_.error } } 180 'the reason on the system drive'
                        Assert ($os.error -match 'TPM' -and $os.protection -ne 'on') "system drive under enforce: $($os | ConvertTo-Json -Compress)"
                        $enforce = $os.error
                    }
                    "drive $($d.Letter): key $keyId kept encrypted, shown with a reason (audited), replaced by $new and retired; system drive under enforce: $enforce"
                } finally {
                    try { Api PUT '/security-pack/bitlocker/policy' @{ mode = 'off' } -Session $s | Out-Null; Wake-Agent $s } catch { }
                    Remove-TestDisk $d
                }
            } | Out-Null
        }

        Check 'T98' 'USB device control (a virtual disk treated as USB storage): devices and file copies recorded, read-only and blocked by policy, an allowed device keeps full access, the drive given back when the policy is off' {
            $s = Login
            $d = New-TestDisk 'ZQUSB'
            Set-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent' -Name UsbTestDevices -Value $d.Pnp
            try {
                Api PUT '/security-pack/usb/policy' @{ mode = 'audit'; auditFiles = $true; message = 'CI: USB drives are controlled.' } -Session $s | Out-Null
                Wake-Agent $s
                # The drive in USB Devices: the agent has the policy and watches the drive.
                $dev = Dep-Wait { @(Api GET '/security-pack/usb/devices' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.assetId -eq $script:agentAsset -and $_.deviceId -eq $d.Pnp } } 180 'the drive in USB Devices'
                # Only the test drive: the runner's own (virtual) system and data disks are never treated as USB storage.
                $others = @(Api GET '/security-pack/usb/devices' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.assetId -eq $script:agentAsset -and $_.deviceId -ne $d.Pnp }
                Assert (-not $others) "other disks treated as USB storage: $(@($others | ForEach-Object { $_.deviceId }) -join ', ')"
                Assert (@(Get-Disk | Where-Object { $_.Number -ne $d.Number -and ($_.IsReadOnly -or $_.IsOffline) }).Count -eq 0) 'another disk was made read-only or offline'
                Start-Sleep 4
                'quarterly figures' | Set-Content "$($d.Letter)\ci-copy.txt"
                $ev = Dep-Wait { @(Api GET "/security-pack/usb/events?kind=files&assetId=$($script:agentAsset)" -Session $s | ForEach-Object { $_ }) | Where-Object { $_.path -like '*ci-copy.txt' } } 180 'the file copy in the console'
                Assert ($ev[0].size -gt 0) "file event: $($ev[0] | ConvertTo-Json -Compress)"
                # Read-only: nothing can be written.
                Api PUT '/security-pack/usb/policy' @{ mode = 'readonly'; auditFiles = $true } -Session $s | Out-Null
                Wake-Agent $s
                Dep-Wait { (Get-Disk -Number $d.Number).IsReadOnly } 120 'the drive made read-only' | Out-Null
                $denied = try { 'x' | Set-Content "$($d.Letter)\ci-denied.txt" -ErrorAction Stop; $false } catch { $true }
                Assert $denied 'writing to the read-only drive worked'
                # Allowed by its ID: full access again.
                $allow = Api POST '/security-pack/usb/allowed' @{ deviceId = $d.Pnp; name = 'CI drive'; access = 'full' } -Session $s
                Wake-Agent $s
                Dep-Wait { -not (Get-Disk -Number $d.Number).IsReadOnly } 120 'the allowed drive writable again' | Out-Null
                # Blocked (exception removed): the device is turned off.
                Api DELETE "/security-pack/usb/allowed/$($allow.id)" -Session $s | Out-Null
                Api PUT '/security-pack/usb/policy' @{ mode = 'block'; auditFiles = $true } -Session $s | Out-Null
                Wake-Agent $s
                Dep-Wait { -not (Get-Volume -FileSystemLabel ZQUSB -ErrorAction SilentlyContinue) } 120 'the drive blocked' | Out-Null
                Dep-Wait { @(Api GET "/security-pack/usb/events?kind=blocked&assetId=$($script:agentAsset)" -Session $s | ForEach-Object { $_ }) } 120 'the block in the console' | Out-Null
                # Off: the agent gives the drive back.
                Api PUT '/security-pack/usb/policy' @{ mode = 'off'; auditFiles = $false } -Session $s | Out-Null
                Wake-Agent $s
                Dep-Wait { Get-Volume -FileSystemLabel ZQUSB -ErrorAction SilentlyContinue } 120 'the drive back after the policy was turned off' | Out-Null
                "file copy recorded ($($ev[0].size) bytes by $($ev[0].user)); read-only refused writing; allowed by ID writable again; blocked (device off); given back when the policy was turned off"
            } finally {
                try { Api PUT '/security-pack/usb/policy' @{ mode = 'off' } -Session $s | Out-Null } catch { }
                Remove-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent' -Name UsbTestDevices -ErrorAction SilentlyContinue
                Remove-TestDisk $d
            }
        } | Out-Null
    }


    if ($agentInstalled) {
        Check 'T29' 'Agent revocation is enforced and uninstall removes the agent' {
            $s = Login
            Api POST "/agents/$($script:agentId)/revoke" -Session $s | Out-Null
            $exe = Join-Path $env:ProgramFiles 'Zentriq Agent\ZentriqAgent.exe'
            Stop-Service ZentriqAgent
            $out = & $exe --once 2>&1 | Out-String
            $status = & $exe --status 2>&1 | Out-String
            Assert ($status -match 'Status:\s+revoked') "agent status after revoke: $status $out"
            $p = Start-Process msiexec.exe -ArgumentList '/x', "`"$agentMsi`"", '/qn', '/l*v', "`"$(Join-Path $OutDir 'agent-uninstall.log')`"" -Wait -PassThru
            Assert ($p.ExitCode -eq 0) "uninstall exit $($p.ExitCode)"
            Assert (-not (Get-Service ZentriqAgent -ErrorAction SilentlyContinue)) 'service still registered'
            Assert (-not (Test-Path 'HKLM:\SOFTWARE\Zentriq\Agent')) 'agent registry state left behind'
            'revoked agent refused; uninstalled cleanly'
        } | Out-Null
    } else { Record 'T29' 'Agent revocation and uninstall' 'BLOCKED' 'agent install failed' }
    Check 'T44' 'Restart a Windows service agentlessly (WinRM, stored credential) on a device without an agent' {
        $s = Login
        Assert $script:agentAsset 'no asset'
        Assert $script:ciCred 'no credential from T28'
        $c = Api GET "/assets/$($script:agentAsset)/control" -Session $s
        Assert ($c.channel -eq 'agentless') "channel $($c.channel) (agent should be gone after T29)"
        $svc = Pick-Service $script:agentAsset $s
        Assert $svc 'no suitable running service in the inventory'
        $bad = Invoke-WebRequest "$base/api/v1/assets/$($script:agentAsset)/services/$svc/restart" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body '{}' -SkipHttpErrorCheck
        Assert ($bad.StatusCode -eq 400 -and (Body $bad) -match 'credentialRequired') "no credential: $($bad.StatusCode)"
        $r = Api POST "/assets/$($script:agentAsset)/services/$svc/restart" @{ credentialId = $script:ciCred } -Session $s
        $a = Wait-Action $script:agentAsset $r.id $s 180
        Assert ($a.status -eq 'succeeded') "action $($a.status): $($a.detail)"
        Assert ((Get-Service $svc).Status -eq 'Running') "$svc is $((Get-Service $svc).Status)"
        "credential prompt enforced; $svc restarted over WinRM: $($a.detail)"
    } | Out-Null

    Check 'T33' 'Enrollment code can be shown again later; revoked codes cannot' {
        $s = Login
        $tok = Api POST '/agents/enrollment-tokens' @{ name = 'ci-show'; validDays = 1; maxUses = 1; serverUrl = 'https://localhost:8444' } -Session $s
        $again = Api GET "/agents/enrollment-tokens/$($tok.id)/code" -Session $s
        Assert ($again.code -eq $tok.code) 'shown code differs from the created code'
        Assert ($again.installCommand -like "*$($tok.code)*") 'install command missing the code'
        Api POST "/agents/enrollment-tokens/$($tok.id)/revoke" -Session $s | Out-Null
        $r = Invoke-WebRequest "$base/api/v1/agents/enrollment-tokens/$($tok.id)/code" -WebSession $s -SkipHttpErrorCheck
        Assert ($r.StatusCode -eq 410) "revoked code returned $($r.StatusCode)"
        $db = Invoke-WebRequest "$base/api/v1/agents/enrollment-tokens" -WebSession $s
        Assert ($db.Content -notmatch [regex]::Escape($tok.code)) 'token list exposes codes'
        $active = Api POST '/agents/enrollment-tokens' @{ name = 'ci-active'; validDays = 1; maxUses = 1; serverUrl = 'https://localhost:8444' } -Session $s
        $refused = Invoke-WebRequest "$base/api/v1/agents/enrollment-tokens/$($active.id)" -Method Delete -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -SkipHttpErrorCheck
        Assert ($refused.StatusCode -eq 409) "deleting an active code returned $($refused.StatusCode)"
        Api DELETE "/agents/enrollment-tokens/$($tok.id)" -Session $s | Out-Null
        Api POST "/agents/enrollment-tokens/$($active.id)/revoke" -Session $s | Out-Null
        $bulk = Api POST '/agents/enrollment-tokens/delete-inactive' @{} -Session $s
        $left = @(Api GET '/agents/enrollment-tokens' -Session $s | ForEach-Object { $_ } | Where-Object { $_.id -in $tok.id, $active.id })
        Assert ($left.Count -eq 0) 'revoked codes still listed after delete'
        "code shown again; revoked code refused (410); list does not expose codes; active code not deletable (409); revoked codes deleted (bulk: $($bulk.deleted))"
    } | Out-Null

    Check 'T34' 'Scan history: re-run with the same settings; delete (single and by age); assets are kept' {
        $s = Login
        $ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -match '^(10\.|172\.(1[6-9]|2\d|3[01])\.|192\.168\.)' -and $_.PrefixOrigin -ne 'WellKnown' } | Select-Object -First 1).IPAddress
        $scan = Api POST '/discovery/scans' @{ name = 'ci-delete-me'; scanType = 'single'; targetsText = $ip } -Session $s
        $deadline = (Get-Date).AddMinutes(3)
        do { Start-Sleep 3; $d = Api GET "/discovery/scans/$($scan.id)" -Session $s } while ($d.scan.status -in 'queued', 'running' -and (Get-Date) -lt $deadline)
        # Re-run: a new scan with the same name and targets; the original is kept.
        $again = Api POST "/discovery/scans/$($scan.id)/rerun" -Session $s
        Assert ($again.id -ne $scan.id -and $again.name -eq 'ci-delete-me') "re-run: $($again | ConvertTo-Json -Compress)"
        $deadline = (Get-Date).AddMinutes(3)
        do { Start-Sleep 3; $d2 = Api GET "/discovery/scans/$($again.id)" -Session $s } while ($d2.scan.status -in 'queued', 'running' -and (Get-Date) -lt $deadline)
        Assert ($d2.scan.status -notin 'queued', 'running' -and $d2.scan.totalTargets -eq $d.scan.totalTargets) "re-run scan: $($d2.scan.status), $($d2.scan.totalTargets) target(s)"
        Assert ((Invoke-WebRequest "$base/api/v1/discovery/scans/$($scan.id)" -WebSession $s -SkipHttpErrorCheck).StatusCode -eq 200) 'original scan gone after re-run'
        Invoke-WebRequest "$base/api/v1/discovery/scans/$($again.id)" -Method Delete -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -SkipHttpErrorCheck | Out-Null
        $r = Invoke-WebRequest "$base/api/v1/discovery/scans/$($scan.id)" -Method Delete -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -SkipHttpErrorCheck
        Assert ($r.StatusCode -eq 204) "delete returned $($r.StatusCode)"
        $g = Invoke-WebRequest "$base/api/v1/discovery/scans/$($scan.id)" -WebSession $s -SkipHttpErrorCheck
        Assert ($g.StatusCode -eq 404) "deleted scan still readable ($($g.StatusCode))"
        $bulk = Api POST '/discovery/scans/delete' @{ olderThanDays = 3650 } -Session $s
        Assert ($null -ne $bulk.deleted) 'bulk delete returned no count'
        $assets = (Api GET '/assets/summary' -Session $s).total
        "re-run started a new scan with the same settings ($($d2.scan.status)); single delete 204, then 404; bulk delete ok ($($bulk.deleted)); assets kept ($assets)"
    } | Out-Null

    Check 'T35' 'Authenticator-app MFA: QR enrollment, sign-in requires the code, disable' {
        Start-Sleep 61   # sign-in is rate limited to 10 per minute per address; start with a fresh window
        function Get-Totp([string] $Base32) {
            $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; $bits = ''
            foreach ($c in $Base32.ToUpper().ToCharArray()) { $bits += [Convert]::ToString($alphabet.IndexOf($c), 2).PadLeft(5, '0') }
            $key = [byte[]]@(for ($i = 0; $i + 8 -le $bits.Length; $i += 8) { [Convert]::ToByte($bits.Substring($i, 8), 2) })
            $step = [long][math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30)
            $counter = [BitConverter]::GetBytes($step); [Array]::Reverse($counter)
            $h = (New-Object Security.Cryptography.HMACSHA1 (, $key)).ComputeHash($counter)
            $o = $h[19] -band 0x0F
            $bin = ([long]($h[$o] -band 0x7F) * 16777216) + ([long]$h[$o + 1] * 65536) + ([long]$h[$o + 2] * 256) + [long]$h[$o + 3]
            ($bin % 1000000).ToString('000000')
        }
        $s = Login
        $e = Api POST '/auth/mfa/enroll' -Session $s
        Assert ($e.qrSvg -match '<svg') 'no QR code SVG returned'
        Assert ($e.otpauthUri -like 'otpauth://totp/*') "otpauth URI: $($e.otpauthUri)"
        Api POST '/auth/mfa/confirm' @{ code = (Get-Totp $e.secret) } -Session $s | Out-Null
        try {
            $body = @{ email = $adminEmail; password = $adminPassword } | ConvertTo-Json
            $r = Invoke-WebRequest "$base/api/v1/auth/login" -Method Post -Body $body -ContentType 'application/json' -Headers @{ 'X-Zentriq-Request' = '1' } -SkipHttpErrorCheck
            Assert ($r.StatusCode -eq 401 -and (Body $r) -match 'mfaRequired') "login without code: $($r.StatusCode) $(Body $r)"
            $s2 = New-Object Microsoft.PowerShell.Commands.WebRequestSession
            $body = @{ email = $adminEmail; password = $adminPassword; mfaCode = (Get-Totp $e.secret) } | ConvertTo-Json
            $r = Invoke-WebRequest "$base/api/v1/auth/login" -Method Post -Body $body -ContentType 'application/json' -Headers @{ 'X-Zentriq-Request' = '1' } -WebSession $s2 -SkipHttpErrorCheck
            Assert ($r.StatusCode -eq 200) "login with code: $($r.StatusCode) $($r.Content)"
            Assert ((Api GET '/auth/me' -Session $s2).mfaEnabled) 'me.mfaEnabled is false'
        } finally {
            if ($s2) { Api POST '/auth/mfa/disable' @{ password = $adminPassword; code = (Get-Totp $e.secret) } -Session $s2 | Out-Null }
        }
        Login | Out-Null   # password-only sign-in works again
        'QR SVG returned; sign-in refused without code, accepted with TOTP; MFA turned off again'
    } | Out-Null

    Check 'T36' 'Forgot password: no account disclosure; zentriqctl reset-password works' {
        Start-Sleep 61
        $o = Invoke-RestMethod "$base/api/v1/auth/options"
        Assert ($o.passwordResetByEmail -eq $false) 'reset by e-mail reported available without SMTP'
        $r = Invoke-WebRequest "$base/api/v1/auth/forgot" -Method Post -Body (@{ email = 'nobody@example.invalid' } | ConvertTo-Json) -ContentType 'application/json' -Headers @{ 'X-Zentriq-Request' = '1' } -SkipHttpErrorCheck
        Assert ($r.StatusCode -eq 503) "forgot without SMTP returned $($r.StatusCode)"
        $r = Invoke-WebRequest "$base/api/v1/auth/reset" -Method Post -Body (@{ token = 'bogus'; newPassword = 'Another-Passw0rd-1' } | ConvertTo-Json) -ContentType 'application/json' -Headers @{ 'X-Zentriq-Request' = '1' } -SkipHttpErrorCheck
        Assert ($r.StatusCode -eq 400) "reset with a bogus token returned $($r.StatusCode)"
        $temp = 'Ci-Reset-' + [Guid]::NewGuid().ToString('N').Substring(0, 10) + '-9'
        try {
            $temp | & $ctl reset-password --email $adminEmail | Out-Host
            Assert ($LASTEXITCODE -eq 0) "zentriqctl reset-password exit $LASTEXITCODE"
            $body = @{ email = $adminEmail; password = $temp } | ConvertTo-Json
            $r = Invoke-WebRequest "$base/api/v1/auth/login" -Method Post -Body $body -ContentType 'application/json' -Headers @{ 'X-Zentriq-Request' = '1' } -SkipHttpErrorCheck
            Assert ($r.StatusCode -eq 200) "sign-in with the reset password: $($r.StatusCode)"
        } finally {
            $adminPassword | & $ctl reset-password --email $adminEmail | Out-Null
        }
        Login | Out-Null
        'options=false, forgot 503 without SMTP, bogus token 400, zentriqctl reset-password then sign-in OK'
    } | Out-Null

    Check 'T37' 'Active Directory connection: validation and a clear error when no DC answers' {
        $s = Login
        $bad = Invoke-WebRequest "$base/api/v1/directory/connections" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' `
            -Body (@{ name = 'ci'; serverHost = '127.0.0.1'; baseDn = 'corp.example.com'; username = 'CORP\x'; password = 'x' } | ConvertTo-Json) -SkipHttpErrorCheck
        Assert ($bad.StatusCode -eq 400 -and (Body $bad) -match 'distinguished name') "invalid DN accepted: $($bad.StatusCode) $(Body $bad)"
        $t = Api POST '/directory/connections/test' @{ name = 'ci'; serverHost = '127.0.0.1'; port = 636; ldaps = $true; baseDn = 'DC=ci,DC=invalid'; username = 'CI\nobody'; password = 'not-a-password' } -Session $s
        Assert (-not $t.ok) 'test reported success without a domain controller'
        Assert ($t.message -match 'reach|LDAP') "message: $($t.message)"
        $c = Api POST '/directory/connections' @{ name = 'ci-dc'; serverHost = '127.0.0.1'; dcName = 'dc01.ci.invalid'; ldaps = $true; baseDn = 'DC=ci,DC=invalid'; username = 'CI\nobody'; password = 'not-a-password' } -Session $s
        $list = Invoke-WebRequest "$base/api/v1/directory/connections" -WebSession $s
        Assert ((Body $list) -notmatch 'not-a-password') 'connection list exposes the password'
        Api DELETE "/directory/connections/$($c.id)" -Session $s | Out-Null
        "invalid DN rejected; no DC: $($t.message); password not returned"
    } | Out-Null

    Check 'T48' 'Reports: run, group, export (XLSX/CSV), injection-safe filters, save, schedule and deliver' {
        $s = Login
        $cat = Api GET '/reports/datasets' -Session $s
        $sets = @($cat.datasets | ForEach-Object { $_ })
        Assert ($sets.Count -ge 9) "only $($sets.Count) dataset(s)"
        Assert ($cat.formats -contains 'pdf') 'PDF is not offered'
        $lib = @(Api GET '/reports' -Session $s | ForEach-Object { $_ })
        Assert (@($lib | Where-Object system).Count -ge 10) "built-in reports: $(@($lib | Where-Object system).Count)"
        $r = Api POST '/reports/run' @{ dataset = 'assets'; definition = @{ columns = @('hostname', 'ip', 'os', 'agent', 'last_contact'); filters = @(@{ column = 'hostname'; op = 'not_empty' }) } } -Session $s
        Assert ($r.columns.Count -eq 5) "columns: $($r.columns.Count)"
        $g = Api POST '/reports/run' @{ dataset = 'assets'; definition = @{ columns = @(); filters = @(); groupBy = @('category') } } -Session $s
        Assert ($g.columns[-1].key -eq 'count') 'grouped run has no count column'
        $inj = Api POST '/reports/run' @{ dataset = 'assets'; definition = @{ columns = @('hostname'); filters = @(@{ column = 'hostname'; op = 'eq'; value = "x'); DROP TABLE zentriq.assets; --" }) } } -Session $s
        Assert ($inj.total -eq 0) 'injection-like value matched rows'
        Assert ((Api GET '/assets?page=1&pageSize=1' -Session $s).total -ge 0) 'assets table damaged'
        $bad = Invoke-WebRequest "$base/api/v1/reports/run" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -SkipHttpErrorCheck `
            -Body (@{ dataset = 'assets'; definition = @{ columns = @('hostname; DROP TABLE x'); filters = @() } } | ConvertTo-Json -Depth 5)
        Assert ($bad.StatusCode -eq 400) "unknown column accepted: $($bad.StatusCode)"
        $spec = @{ columns = @('hostname', 'ip', 'os'); filters = @() }
        $x = Invoke-WebRequest "$base/api/v1/reports/export" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body (@{ dataset = 'assets'; definition = $spec; format = 'xlsx'; name = 'CI devices' } | ConvertTo-Json -Depth 5)
        Assert ($x.RawContentStream.ToArray()[0] -eq 0x50 -and $x.RawContentStream.ToArray()[1] -eq 0x4B) 'xlsx is not a ZIP package'
        $xlsx = Join-Path $OutDir 'ci-report.xlsx'; [IO.File]::WriteAllBytes($xlsx, $x.RawContentStream.ToArray())
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($xlsx); $names = $zip.Entries.FullName; $zip.Dispose()
        Assert ($names -contains 'xl/worksheets/sheet1.xml') "xlsx parts: $($names -join ',')"
        $c = Invoke-WebRequest "$base/api/v1/reports/export" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body (@{ dataset = 'assets'; definition = $spec; format = 'csv' } | ConvertTo-Json -Depth 5)
        Assert ($c.Content -match 'Hostname') 'CSV has no header'
        $new = Api POST '/reports' @{ name = 'CI report'; dataset = 'software'; definition = @{ columns = @('software', 'version', 'hostname'); filters = @() }; shared = $false } -Session $s
        $sch = Api PUT "/reports/$($new.id)/schedule" @{ schedule = @{ enabled = $true; frequency = 'weekly'; hour = 6; weekday = 1; day = 1 }; recipients = @('ci@example.invalid'); format = 'xlsx' } -Session $s
        Assert ($sch.nextRunAt) 'schedule has no next run'
        Api POST "/reports/$($new.id)/send" -Session $s | Out-Null
        $deadline = (Get-Date).AddMinutes(3)
        do { Start-Sleep 5; $run = @((Api GET "/reports/runs?reportId=$($new.id)" -Session $s).items)[0] } while ((-not $run -or $run.status -eq 'running') -and (Get-Date) -lt $deadline)
        Assert ($run -and $run.status -ne 'running') 'delivery did not finish'
        Assert ($run.downloadable) 'run file not kept'
        $f = Invoke-WebRequest "$base/api/v1/reports/runs/$($run.id)/file" -WebSession $s
        Assert ($f.RawContentStream.Length -gt 100) 'run file empty'
        Api DELETE "/reports/$($new.id)" -Session $s | Out-Null
        "$($sets.Count) datasets, $($lib.Count) reports; xlsx $((Get-Item $xlsx).Length) bytes; delivery '$($run.status)': $($run.error)"
    } | Out-Null

    Check 'T49' 'Vulnerability management: NVD file import, CPE mapping, evaluation, risk acceptance' {
        $s = Login
        $maps = @((Api GET '/vulnerabilities/products?pageSize=200' -Session $s).items)
        $pick = @($maps | Select-Object -First 3)
        foreach ($m in $pick) { Api PUT "/vulnerabilities/products/$($m.id)" @{ status = 'manual'; vendor = 'zentriqci'; product = 'ci_product' } -Session $s | Out-Null }
        $nvd = @{ resultsPerPage = 1; startIndex = 0; totalResults = 1; format = 'NVD_CVE'; version = '2.0'; vulnerabilities = @(@{ cve = @{
            id = 'CVE-2099-4242'; published = '2099-01-02T10:15:00.000'; lastModified = '2099-01-03T08:00:00.000'; vulnStatus = 'Analyzed'
            descriptions = @(@{ lang = 'en'; value = 'Zentriq CI test vulnerability.' })
            metrics = @{ cvssMetricV31 = @(@{ type = 'Primary'; cvssData = @{ version = '3.1'; vectorString = 'CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H'; baseScore = 9.8 } }) }
            configurations = @(@{ nodes = @(@{ operator = 'OR'; negate = $false; cpeMatch = @(@{ vulnerable = $true; criteria = 'cpe:2.3:a:zentriqci:ci_product:*:*:*:*:*:*:*:*'; versionEndExcluding = '999999' }) }) })
            references = @(@{ url = 'https://example.invalid/ci' }) } }) } | ConvertTo-Json -Depth 12
        $up = Invoke-RestMethod "$base/api/v1/vulnerabilities/import?source=nvd&name=ci-nvd.json" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/octet-stream' -Body ([Text.Encoding]::UTF8.GetBytes($nvd))
        $deadline = (Get-Date).AddMinutes(4)
        do { Start-Sleep 5; $imp = @(Api GET '/vulnerabilities/imports' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $up.id } while ($imp.status -in 'queued', 'running' -and (Get-Date) -lt $deadline)
        Assert ($imp.status -eq 'done') "import $($imp.status): $($imp.message)"
        $cve = Api GET '/vulnerabilities/cves/CVE-2099-4242' -Session $s
        Assert ($cve.cve.severity -eq 'critical' -and $cve.products.Count -eq 1) 'CVE not stored with severity and product range'
        Start-Sleep 10
        $f = Api GET '/vulnerabilities/findings?cve=CVE-2099-4242&status=all' -Session $s
        $detail = "import ok; $($pick.Count) product(s) mapped; $($f.total) finding(s)"
        if ($pick.Count -gt 0 -and $f.total -gt 0) {
            Assert ($f.items[0].priority -in 'P1', 'P2') "priority $($f.items[0].priority)"
            $acc = Api POST '/vulnerabilities/findings/status' @{ ids = @($f.items[0].id); status = 'accepted'; reason = 'CI risk acceptance test'; until = (Get-Date).AddDays(30).ToUniversalTime().ToString('o') } -Session $s
            Assert ($acc.updated -eq 1) 'risk acceptance not applied'
            $noReason = Invoke-WebRequest "$base/api/v1/vulnerabilities/findings/status" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -SkipHttpErrorCheck -Body (@{ ids = @($f.items[0].id); status = 'accepted' } | ConvertTo-Json)
            Assert ($noReason.StatusCode -eq 400) 'acceptance without reason/expiry accepted'
            Api POST '/vulnerabilities/findings/status' @{ ids = @($f.items[0].id); status = 'open' } -Session $s | Out-Null
            $detail += "; $($f.items[0].priority) risk $($f.items[0].risk); acceptance and re-open OK"
        } elseif ($pick.Count -gt 0) { throw "no finding for the mapped product(s) $($pick.software -join ', ')" }
        $o = Api GET '/vulnerabilities/overview' -Session $s
        Assert ($o.kpi.cveCatalogue -ge 1) 'overview does not count the CVE'
        foreach ($m in $pick) { Api PUT "/vulnerabilities/products/$($m.id)" @{ status = 'auto' } -Session $s | Out-Null }
        $detail
    } | Out-Null

    Check 'T50' 'Windows vulnerabilities: Microsoft file import, build matching, KB, workflow (in progress, ticket, note, history), remediation plan' {
        $s = Login
        $o = Api GET '/vulnerabilities/overview' -Session $s
        Assert ($o.kpi.windowsDevices -ge 1) 'no Windows device with an inventory document (T28 agentless inventory of this server)'
        Assert ($o.kpi.windowsNoBuild -lt $o.kpi.windowsDevices) "no Windows device reports a full build (UBR): $($o.kpi.windowsNoBuild) of $($o.kpi.windowsDevices) without"
        # Synthetic monthly document: one exploited CVE fixed in a build far above this server's (Windows Server 2022, 10.0.20348).
        $cvrf = @{
            DocumentTitle = @{ Value = 'Zentriq CI Security Updates' }
            DocumentTracking = @{ Identification = @{ ID = @{ Value = '2099-Dec' } }; CurrentReleaseDate = '2099-12-08T08:00:00Z' }
            ProductTree = @{ FullProductName = @(@{ ProductID = '90001'; Value = 'Windows Server 2022' }, @{ ProductID = '90002'; Value = 'Windows Server 2022 (Server Core installation)' }) }
            Vulnerability = @(@{
                Title = @{ Value = 'Zentriq CI Windows Kernel Elevation of Privilege Vulnerability' }
                CVE = 'CVE-2099-5050'
                Notes = @(@{ Title = 'Description'; Type = 2; Value = '<p>CI test vulnerability.</p>' })
                Threats = @(@{ Type = 3; Description = @{ Value = 'Important' }; ProductID = @('90001') }, @{ Type = 1; Description = @{ Value = 'Publicly Disclosed:No;Exploited:Yes;Latest Software Release:Exploitation Detected' } })
                CVSSScoreSets = @(@{ BaseScore = 7.8; Vector = 'CVSS:3.1/AV:L/AC:L/PR:L/UI:N/S:U/C:H/I:H/A:H'; ProductID = @('90001') })
                Remediations = @(@{ Description = @{ Value = '5999999' }; URL = 'https://catalog.update.microsoft.com/v7/site/Search.aspx?q=KB5999999'; ProductID = @('90001', '90002'); Type = 2; SubType = 'Security Update'; FixedBuild = '10.0.20348.99999'; RestartRequired = @{ Value = 'Yes' } })
                RevisionHistory = @(@{ Number = '1.0'; Date = '2099-12-08T08:00:00' })
            })
        } | ConvertTo-Json -Depth 12
        $up = Invoke-RestMethod "$base/api/v1/vulnerabilities/import?source=msrc&name=ci-msrc.json" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/octet-stream' -Body ([Text.Encoding]::UTF8.GetBytes($cvrf))
        $deadline = (Get-Date).AddMinutes(4)
        do { Start-Sleep 5; $imp = @(Api GET '/vulnerabilities/imports' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $up.id } while ($imp.status -in 'queued', 'running' -and (Get-Date) -lt $deadline)
        Assert ($imp.status -eq 'done') "import $($imp.status): $($imp.message)"
        $deadline = (Get-Date).AddMinutes(2)
        do { Start-Sleep 5; $f = Api GET '/vulnerabilities/findings?cve=CVE-2099-5050&status=all' -Session $s } while ($f.total -eq 0 -and (Get-Date) -lt $deadline)
        $o = Api GET '/vulnerabilities/overview' -Session $s
        Assert ($f.total -ge 1) "no Windows finding (Windows devices $($o.kpi.windowsDevices), without build $($o.kpi.windowsNoBuild); evaluation: $(($o.feeds | Where-Object source -eq 'evaluate').message))"
        $x = $f.items[0]
        Assert ($x.source -eq 'os' -and $x.kb -eq '5999999' -and $x.fixedVersion -eq '10.0.20348.99999') "finding: source $($x.source), KB $($x.kb), fixed $($x.fixedVersion)"
        Assert ($x.priority -eq 'P1' -and $x.exploited) "priority $($x.priority) (Microsoft 'exploited' must give P1)"
        $cve = Api GET '/vulnerabilities/cves/CVE-2099-5050' -Session $s
        Assert ($cve.cve.msExploited -and $cve.cve.msSeverity -eq 'Important' -and @($cve.windows).Count -eq 2) 'CVE detail lacks the Microsoft data'
        # Workflow: start (assigns to me), ticket, note; history keeps all three.
        Assert ((Api POST '/vulnerabilities/findings/workflow' @{ ids = @($x.id); action = 'start' } -Session $s).updated -eq 1) 'start remediation failed'
        Api POST '/vulnerabilities/findings/workflow' @{ ids = @($x.id); action = 'ticket'; ticket = 'CHG-CI-1' } -Session $s | Out-Null
        Api POST '/vulnerabilities/findings/workflow' @{ ids = @($x.id); action = 'note'; note = 'Patch window Saturday' } -Session $s | Out-Null
        $d = Api GET "/vulnerabilities/findings/$($x.id)" -Session $s
        Assert ($d.finding.status -eq 'in_progress' -and $d.finding.assignee -and $d.finding.ticket -eq 'CHG-CI-1') "after workflow: $($d.finding.status), $($d.finding.assignee), $($d.finding.ticket)"
        Assert (@($d.events).Count -ge 3) "history has $(@($d.events).Count) entries"
        Assert ($d.finding.remediation -match 'KB5999999') "remediation text: $($d.finding.remediation)"
        $mine = Api GET '/vulnerabilities/findings?assignee=me&status=in_progress' -Session $s
        Assert ($mine.total -ge 1) 'not listed under assigned to me'
        # Re-evaluation keeps the workflow state.
        Api POST '/vulnerabilities/evaluate' -Session $s | Out-Null
        Start-Sleep 20
        Assert ((Api GET "/vulnerabilities/findings/$($x.id)" -Session $s).finding.status -eq 'in_progress') 're-evaluation reset the status'
        $plan = Api GET '/vulnerabilities/remediations?source=os' -Session $s
        $line = @($plan.items) | Where-Object { $_.product -eq 'Windows Server 2022' } | Select-Object -First 1
        Assert ($line -and $line.kb -eq '5999999' -and $line.action -match 'KB5999999') "remediation plan: $(@($plan.items).Count) line(s)"
        $o = Api GET '/vulnerabilities/overview' -Session $s
        Assert ($o.kpi.osOpen -ge 1 -and $o.kpi.inProgress -ge 1 -and @($o.aging).Count -ge 1) 'overview lacks OS / workflow / aging figures'
        "$($f.total) Windows finding(s) on $($x.hostname) ($($x.version)); P1 risk $($x.risk); workflow + history ($(@($d.events).Count) entries); plan: $($line.action)"
    } | Out-Null

    Check 'T51' "Live Microsoft data: 'Update now' loads Windows fixed builds from api.msrc.microsoft.com and evaluates this server" {
        $s = Login
        Api PUT '/vulnerabilities/settings' @{ msrcMonths = 3 } -Session $s | Out-Null
        $before = (Api GET '/vulnerabilities/overview' -Session $s).feeds | Where-Object source -eq 'msrc'
        Api POST '/vulnerabilities/sync' -Session $s | Out-Null
        $deadline = (Get-Date).AddMinutes(25)
        do {
            Start-Sleep 20
            $o = Api GET '/vulnerabilities/overview' -Session $s
            $m = $o.feeds | Where-Object source -eq 'msrc'
            $e = $o.feeds | Where-Object source -eq 'evaluate'
        } while (($m.lastRunAt -eq $before.lastRunAt -or -not $e.lastRunAt -or [datetime]$e.lastRunAt -lt [datetime]$m.lastRunAt) -and (Get-Date) -lt $deadline)
        if ($m.lastRunAt -eq $before.lastRunAt) { throw 'the update did not reach the Microsoft stage in 25 minutes' }
        if (-not $m.lastOkAt -or $m.lastOkAt -ne $m.lastRunAt) {
            if ($m.message -match 'Could not reach|answered 5\d\d') { throw "BLOCKED: api.msrc.microsoft.com not reachable from the runner: $($m.message)" }
            throw "Microsoft feed failed: $($m.message)"
        }
        Assert ($o.kpi.windowsCves -ge 50) "only $($o.kpi.windowsCves) Windows CVE(s) parsed from 3 months of Microsoft data: $($m.message)"
        $os = Api GET '/vulnerabilities/findings?source=os&status=active&pageSize=5' -Session $s
        $real = @($os.items | Where-Object { $_.cve -ne 'CVE-2099-5050' })
        "$($m.message) This runner: $($os.total) open Windows finding(s)$(if ($real.Count) { "; e.g. $($real[0].cve) fixed by KB$($real[0].kb) (build $($real[0].fixedVersion), runner $($real[0].version))" } else { ' (fully patched)' })."
    } | Out-Null

    Check 'T52' 'Report analytics: calculations, date grouping, new data sources, PDF, saved report on the dashboard' {
        $s = Login
        $agg = Api POST '/reports/run' @{ dataset = 'vulnerabilities'; definition = @{ columns = @('severity'); groupBy = @('severity'); filters = @();
            aggregates = @(@{ fn = 'count_distinct'; column = 'hostname' }, @{ fn = 'avg'; column = 'cvss' }, @{ fn = 'max'; column = 'risk' }); sort = @(@{ column = 'agg_max_risk'; dir = 'desc' }) } } -Session $s
        Assert (($agg.columns | ForEach-Object key) -join ',' -eq 'severity,count,agg_count_distinct_hostname,agg_avg_cvss,agg_max_risk') "columns: $(($agg.columns | ForEach-Object key) -join ',')"
        $month = Api POST '/reports/run' @{ dataset = 'assets'; definition = @{ columns = @('first_seen'); groupBy = @('first_seen'); dateBucket = 'month'; filters = @() } } -Session $s
        Assert (@($month.rows).Count -ge 1 -and $month.rows[0][0] -match '^\d{4}-\d{2}$') "month bucket: $($month.rows[0][0])"
        foreach ($d in 'software_titles', 'discovery', 'audit') {
            $r = Api POST '/reports/run' @{ dataset = $d; definition = @{ columns = @(((Api GET '/reports/datasets' -Session $s).datasets | Where-Object key -eq $d).columns | Select-Object -First 4 | ForEach-Object key); filters = @() } } -Session $s
            Assert ($null -ne $r.total) "$d did not run"
        }
        $titles = Api POST '/reports/run' @{ dataset = 'software_titles'; definition = @{ columns = @('software', 'devices', 'latest_version'); filters = @(); sort = @(@{ column = 'devices'; dir = 'desc' }) } } -Session $s
        Assert ($titles.total -ge 1) 'no software titles'
        $lib = @(Api GET '/reports' -Session $s | ForEach-Object { $_ })
        $win = $lib | Where-Object { $_.system -and $_.name -eq 'Windows updates missing' }
        $def = Api GET "/reports/$($win.id)" -Session $s
        $pdf = Invoke-WebRequest "$base/api/v1/reports/export" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' `
            -Body (@{ dataset = $def.dataset; definition = $def.definition; format = 'pdf'; name = $def.name } | ConvertTo-Json -Depth 8)
        $bytes = $pdf.RawContentStream.ToArray()
        Assert ([Text.Encoding]::ASCII.GetString($bytes, 0, 5) -eq '%PDF-' -and $pdf.Headers['Content-Type'] -match 'pdf') 'export is not a PDF'
        $pdfFile = Join-Path $OutDir 'ci-windows-updates.pdf'; [IO.File]::WriteAllBytes($pdfFile, $bytes)
        $sum = $lib | Where-Object { $_.system -and $_.name -eq 'Vulnerability summary by severity' }
        $sumPdf = Invoke-WebRequest "$base/api/v1/reports/export" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' `
            -Body (@{ dataset = 'vulnerabilities'; definition = (Api GET "/reports/$($sum.id)" -Session $s).definition; format = 'pdf'; name = $sum.name } | ConvertTo-Json -Depth 8)
        [IO.File]::WriteAllBytes((Join-Path $OutDir 'ci-vuln-summary.pdf'), $sumPdf.RawContentStream.ToArray())
        # A saved report on the dashboard.
        $dash = @(Api GET '/dashboards' -Session $s | ForEach-Object { $_ }) | Where-Object owned | Select-Object -First 1
        $widgets = @(@{ type = 'kpi.tiles'; title = $null; width = 12; height = 2; settings = @{} }, @{ type = 'report'; title = $sum.name; width = 4; height = 2; settings = @{ reportId = $sum.id; chart = 'donut' } })
        $body = @{ name = 'My dashboard'; shared = $false; filters = @{}; refreshSeconds = 60; widgets = $widgets }
        if ($dash) { Api PUT "/dashboards/$($dash.id)" $body -Session $s | Out-Null } else { Api POST '/dashboards' $body -Session $s | Out-Null }
        $saved = @(Api GET '/dashboards' -Session $s | ForEach-Object { $_ }) | Where-Object owned | Select-Object -First 1
        $w = @($saved.widgets) | Where-Object type -eq 'report'
        Assert ($w -and $w.settings.reportId -eq $sum.id) 'report widget not saved'
        $badWidget = Invoke-WebRequest "$base/api/v1/dashboards/$($saved.id)" -Method Put -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -SkipHttpErrorCheck `
            -Body (@{ name = 'x'; shared = $false; filters = @{}; refreshSeconds = 60; widgets = @(@{ type = 'report'; width = 4; height = 2; settings = @{} }) } | ConvertTo-Json -Depth 6)
        Assert ($badWidget.StatusCode -eq 400) "report widget without a report accepted: $($badWidget.StatusCode)"
        "aggregates and month buckets OK; 3 new data sources; PDF $($bytes.Length) bytes; report widget saved"
    } | Out-Null

    Check 'T53' 'Security configuration: Windows settings collected, 30 checks evaluated, score, baseline, exception, report' {
        $s = Login
        # Collect inventory again so this server reports its security settings with this build's collector.
        Api POST '/assets/collect' @{ all = $true } -Session $s | Out-Null
        $deadline = (Get-Date).AddMinutes(8)
        $evaluated = $false
        do {
            Start-Sleep 20
            $o = Api GET '/security/overview' -Session $s
            if ($o.coverage.windowsDevices -ge 1 -and $o.coverage.noSettingsData -lt $o.coverage.windowsDevices -and -not $evaluated) {
                Api POST '/security/evaluate' -Session $s | Out-Null; $evaluated = $true; Start-Sleep 20
                $o = Api GET '/security/overview' -Session $s
            }
        } while (-not ($evaluated -and $o.kpi.devices -ge 1) -and (Get-Date) -lt $deadline)
        Assert ($o.coverage.windowsDevices -ge 1) 'no Windows device with inventory'
        Assert ($o.coverage.noSettingsData -lt $o.coverage.windowsDevices) "no Windows device reported its security settings ($($o.coverage.noSettingsData) of $($o.coverage.windowsDevices) without)"
        Assert ($o.kpi.devices -ge 1 -and $null -ne $o.kpi.avgScore) "not evaluated: $($o.kpi | ConvertTo-Json -Compress)"
        $dev = (Api GET '/security/devices?sort=worst&pageSize=50' -Session $s).items | Where-Object { $null -ne $_.score } | Select-Object -First 1
        Assert $dev 'no scored device'
        $d = Api GET "/security/devices/$($dev.assetId)" -Session $s
        Assert ($d.settingsReported) 'device page: settings not reported'
        $known = @($d.results | Where-Object { $_.status -in 'pass', 'fail' })
        Assert ($known.Count -ge 15) "only $($known.Count) checks could be decided on $($dev.hostname): unknown = $(@($d.results | Where-Object status -eq 'unknown' | ForEach-Object rule) -join ',')"
        foreach ($must in 'SC-02', 'SC-11', 'SC-15', 'SC-20', 'SC-22') {
            $r = $d.results | Where-Object rule -eq $must | Select-Object -First 1
            Assert ($r -and $r.status -in 'pass', 'fail') "$must is $($r.status) ($($r.actual))"
        }
        $rules = (Api GET '/security/rules' -Session $s).rules
        Assert (@($rules).Count -eq 43 -and @($rules | Where-Object enabled).Count -eq 40) "rules: $(@($rules).Count), on $(@($rules | Where-Object enabled).Count)"
        $failing = @($d.results | Where-Object status -eq 'fail')
        $target = if ($failing.Count) { $failing[0] } else { $known[0] }
        $rd = Api GET "/security/rules/$($target.rule)?status=" -Session $s
        Assert ($rd.rule.remediation.Length -gt 20 -and @($rd.devices).Count -ge 1) 'check detail lacks guidance or devices'
        # A baseline for servers with a stricter patch window; then an exception for this device.
        $builtIn = @(Api GET '/security/baselines' -Session $s | ForEach-Object { $_ }) | Where-Object builtIn
        $nb = Api POST '/security/baselines' @{ name = 'CI servers'; categories = @('server'); copyFrom = $builtIn.id } -Session $s
        $det = Api GET "/security/baselines/$($nb.id)" -Session $s
        $body = @{ name = 'CI servers'; description = 'CI'; enabled = $true; categories = @('server'); rules = @($det.rules | ForEach-Object { @{ id = $_.id; enabled = $_.enabled; severity = $null; params = $(if ($_.id -eq 'SC-07') { @{ maxDays = 7 } } else { @{} }) } }) }
        Api PUT "/security/baselines/$($nb.id)" $body -Session $s | Out-Null
        Assert (((Api GET "/security/baselines/$($nb.id)" -Session $s).rules | Where-Object id -eq 'SC-07').params[0].value -eq 7) 'baseline parameter not saved'
        $bad = Invoke-WebRequest "$base/api/v1/security/baselines/$($nb.id)" -Method Put -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -SkipHttpErrorCheck `
            -Body (@{ name = 'CI servers'; rules = @(@{ id = 'SC-07'; enabled = $true; params = @{ maxDays = 1 } }) } | ConvertTo-Json -Depth 5)
        Assert ($bad.StatusCode -eq 400) "out-of-range parameter accepted: $($bad.StatusCode)"
        Api POST '/security/exceptions' @{ ruleId = $target.rule; assetIds = @($dev.assetId); reason = 'CI exception test'; expiresAt = (Get-Date).AddDays(30).ToUniversalTime().ToString('o') } -Session $s | Out-Null
        Api POST '/security/evaluate' -Session $s | Out-Null
        $deadline = (Get-Date).AddMinutes(3)
        do { Start-Sleep 10; $after = Api GET "/security/devices/$($dev.assetId)" -Session $s } while ((($after.results | Where-Object { $_.rule -eq $target.rule -and $_.baselineId -eq $builtIn.id }).status -ne 'exempt' -or @($after.baselines).Count -lt 2) -and (Get-Date) -lt $deadline)
        Assert (($after.results | Where-Object { $_.rule -eq $target.rule -and $_.baselineId -eq $builtIn.id }).status -eq 'exempt') 'exception not applied'
        Assert (@($after.baselines).Count -ge 2) 'the server baseline was not evaluated'
        $rep = Api POST '/reports/run' @{ dataset = 'security'; definition = @{ columns = @('hostname', 'rule', 'title', 'status', 'actual', 'baseline'); filters = @() } } -Session $s
        Assert ($rep.total -ge 15) "security report: $($rep.total) rows"
        $lib = @(Api GET '/reports' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.system -and $_.dataset -eq 'security' }
        Assert (@($lib).Count -ge 2) 'built-in security reports missing'
        foreach ($x in @(Api GET '/security/exceptions' -Session $s | ForEach-Object { $_ }) | Where-Object reason -eq 'CI exception test') { Api DELETE "/security/exceptions/$($x.id)" -Session $s | Out-Null }
        Api DELETE "/security/baselines/$($nb.id)" -Session $s | Out-Null
        $fail = @($d.results | Where-Object status -eq 'fail' | ForEach-Object rule)
        "$($dev.hostname): score $($dev.score); $(@($d.results | Where-Object status -eq 'pass').Count) passed, $($fail.Count) failed ($($fail -join ', ')), $(@($d.results | Where-Object status -eq 'unknown').Count) unknown; baseline + exception + report OK"
    } | Out-Null

    Check 'T54' 'Integrations: signed webhook and syslog (CEF) channels, test messages, alert rule delivers an event, failures logged' {
        $s = Login
        $hookFile = Join-Path $OutDir 'ci-webhook.jsonl'; $sysFile = Join-Path $OutDir 'ci-syslog.txt'
        Remove-Item $hookFile, $sysFile -ErrorAction SilentlyContinue
        $hook = Start-ThreadJob -ArgumentList $hookFile -ScriptBlock {
            param($file)
            $l = [Net.HttpListener]::new(); $l.Prefixes.Add('http://127.0.0.1:18099/hook/'); $l.Start()
            $deadline = (Get-Date).AddMinutes(8); $t = $null
            while ((Get-Date) -lt $deadline) {
                if (-not $t) { $t = $l.GetContextAsync() }
                if (-not $t.Wait(3000)) { continue }
                $c = $t.Result; $t = $null
                $body = [IO.StreamReader]::new($c.Request.InputStream, [Text.Encoding]::UTF8).ReadToEnd()
                [ordered]@{ body = $body; sig = $c.Request.Headers['X-Zentriq-Signature']; ts = $c.Request.Headers['X-Zentriq-Timestamp']; ev = $c.Request.Headers['X-Zentriq-Event']; key = $c.Request.Headers['X-Api-Key'] } | ConvertTo-Json -Compress | Add-Content $file
                $c.Response.StatusCode = 204; $c.Response.Close()
            }
            $l.Stop()
        }
        $sys = Start-ThreadJob -ArgumentList $sysFile -ScriptBlock {
            param($file)
            $u = [Net.Sockets.UdpClient]::new(15514); $u.Client.ReceiveTimeout = 3000
            $ep = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0); $deadline = (Get-Date).AddMinutes(8)
            while ((Get-Date) -lt $deadline) { try { $b = $u.Receive([ref]$ep); Add-Content $file ([Text.Encoding]::UTF8.GetString($b)) } catch { } }
            $u.Close()
        }
        Start-Sleep 3
        try {
            $hmacKey = 'ci-' + [guid]::NewGuid().ToString('N')
            $wh = Api POST '/integrations/channels' @{ kind = 'webhook'; name = 'CI webhook'; settings = @{ url = 'http://127.0.0.1:18099/hook/'; headerName = 'X-Api-Key' }; secrets = @{ signingSecret = $hmacKey; headerValue = 'ci-key' } } -Session $s
            $sl = Api POST '/integrations/channels' @{ kind = 'syslog'; name = 'CI syslog'; settings = @{ host = '127.0.0.1'; port = 15514; protocol = 'udp'; format = 'cef' } } -Session $s
            $badSlack = Invoke-WebRequest "$base/api/v1/integrations/channels" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -SkipHttpErrorCheck `
                -Body (@{ kind = 'slack'; name = 'bad'; secrets = @{ url = 'http://hooks.slack.com/x' } } | ConvertTo-Json -Depth 4)
            Assert ($badSlack.StatusCode -eq 400) "Slack channel with an http:// URL accepted: $($badSlack.StatusCode)"
            $list = Invoke-WebRequest "$base/api/v1/integrations/channels" -WebSession $s
            Assert (-not (Body $list).Contains($hmacKey) -and -not (Body $list).Contains('ci-key')) 'a channel secret is returned by the API'
            $t1 = Api POST "/integrations/channels/$($wh.id)/test" -Session $s
            Assert ($t1.ok) "webhook test failed: $($t1.error)"
            $t2 = Api POST "/integrations/channels/$($sl.id)/test" -Session $s
            Assert ($t2.ok) "syslog test failed: $($t2.error)"
            $deadline = (Get-Date).AddSeconds(30)
            while (-not ((Test-Path $hookFile) -and (Test-Path $sysFile)) -and (Get-Date) -lt $deadline) { Start-Sleep 2 }
            $first = Get-Content $hookFile -ErrorAction SilentlyContinue | Select-Object -First 1 | ConvertFrom-Json
            Assert ($first -and $first.ev -eq 'test' -and $first.key -eq 'ci-key') "webhook not received: $($first | ConvertTo-Json -Compress)"
            $mac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($hmacKey))
            $expected = 'sha256=' + ([Convert]::ToHexString($mac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$($first.ts).$($first.body)")))).ToLowerInvariant()
            Assert ($first.sig -eq $expected) "signature $($first.sig) does not match $expected"
            $line = Get-Content $sysFile -ErrorAction SilentlyContinue | Select-Object -First 1
            Assert ($line -match '^<134>1 \S+ \S+ Zentriq - test - CEF:0\|Zentriq\|Zentriq\|[^|]+\|test\|') "syslog line: $line"
            # An alert rule sends a real event: a new device.
            Api POST '/integrations/rules' @{ name = 'CI new devices'; eventType = 'asset.discovered'; minSeverity = 'info'; channelIds = @($wh.id, $sl.id); throttleMinutes = 0 } -Session $s | Out-Null
            Start-Sleep 5
            $asset = Api POST '/assets' @{ hostname = 'CI-INTEGRATION-DEVICE'; category = 'other'; osFamily = 'other'; criticality = 'Low' } -Session $s
            $deadline = (Get-Date).AddMinutes(4)
            do { Start-Sleep 10; $got = @(Get-Content $hookFile -ErrorAction SilentlyContinue | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object ev -eq 'asset.discovered') } while ($got.Count -eq 0 -and (Get-Date) -lt $deadline)
            Assert ($got.Count -ge 1) 'no asset.discovered webhook within 4 minutes'
            $evBody = $got[0].body | ConvertFrom-Json
            Assert ($evBody.asset.hostname -eq 'CI-INTEGRATION-DEVICE' -and $evBody.type -eq 'asset.discovered') "event body: $($got[0].body)"
            Start-Sleep 3
            Assert (@(Get-Content $sysFile | Where-Object { $_ -match '\|asset\.discovered\|' -and $_ -match 'CI-INTEGRATION-DEVICE' }).Count -ge 1) 'asset event not in syslog'
            $log = Api GET '/integrations/deliveries?status=sent' -Session $s
            Assert (@($log.items | Where-Object eventType -eq 'asset.discovered').Count -ge 2) 'delivery log lacks the sent event'
            # A channel that cannot be reached: the test reports the error and the channel shows it.
            $dead = Api POST '/integrations/channels' @{ kind = 'webhook'; name = 'CI unreachable'; settings = @{ url = 'http://127.0.0.1:1/none' } } -Session $s
            $t3 = Api POST "/integrations/channels/$($dead.id)/test" -Session $s
            Assert (-not $t3.ok -and $t3.error) 'unreachable webhook reported as sent'
            Assert ((@(Api GET '/integrations/channels' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $dead.id).lastError) 'channel error not recorded'
            Api POST "/assets/$($asset.id)/retire" -Session $s | Out-Null
            "webhook HMAC verified; CEF syslog: $($line.Substring(0, [Math]::Min(90, $line.Length)))…; rule delivered asset.discovered to 2 channels; failure logged: $($t3.error)"
        } finally {
            Stop-Job $hook, $sys -ErrorAction SilentlyContinue; Remove-Job $hook, $sys -Force -ErrorAction SilentlyContinue
        }
    } | Out-Null

    Check 'T55' 'API tokens: read-only bearer access to report data, permissions limited, revocation' {
        $s = Login
        $bad = Invoke-WebRequest "$base/api/v1/integrations/tokens" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -SkipHttpErrorCheck `
            -Body (@{ name = 'bad'; permissions = @('users.manage') } | ConvertTo-Json)
        Assert ($bad.StatusCode -eq 400) "token with a write permission accepted: $($bad.StatusCode)"
        $tok = Api POST '/integrations/tokens' @{ name = 'CI token'; permissions = @('reports.export', 'assets.view', 'security.view'); expiresInDays = 1 } -Session $s
        Assert ($tok.token -like 'zq_*') 'no token returned'
        $h = @{ Authorization = "Bearer $($tok.token)" }
        $all = @(Api GET '/reports' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.system -and $_.name -eq 'All assets' }
        $csv = Invoke-WebRequest "$base/api/v1/reports/$($all.id)/data?format=csv" -Headers $h -SkipHttpErrorCheck
        Assert ($csv.StatusCode -eq 200 -and (Body $csv) -match 'Hostname') "CSV with token: $($csv.StatusCode)"
        $json = Invoke-RestMethod "$base/api/v1/reports/$($all.id)/data" -Headers $h
        Assert ($json.total -ge 1 -and $null -ne $json.rows[0].hostname) 'JSON rows missing'
        Assert ((Invoke-WebRequest "$base/api/v1/security/overview" -Headers $h -SkipHttpErrorCheck).StatusCode -eq 200) 'security overview with token'
        Assert ((Invoke-WebRequest "$base/api/v1/vulnerabilities/overview" -Headers $h -SkipHttpErrorCheck).StatusCode -eq 403) 'token without vuln.view could read vulnerabilities'
        $post = Invoke-WebRequest "$base/api/v1/security/evaluate" -Method Post -Headers ($h + @{ 'X-Zentriq-Request' = '1' }) -SkipHttpErrorCheck
        Assert ($post.StatusCode -eq 401) "POST with a token returned $($post.StatusCode)"
        $listed = @(Api GET '/integrations/tokens' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $tok.id
        Assert ($listed.lastUsedAt) 'last use not recorded'
        Api DELETE "/integrations/tokens/$($tok.id)" -Session $s | Out-Null
        Assert ((Invoke-WebRequest "$base/api/v1/reports/$($all.id)/data" -Headers $h -SkipHttpErrorCheck).StatusCode -eq 401) 'revoked token still works'
        "CSV $((Body $csv).Length) bytes and $($json.total) JSON row(s) with the token; 403 without permission; POST refused; revoked -> 401"
    } | Out-Null

    Check 'T56' 'Departments: create, assign assets, filter, dashboard and reports, rename, delete' {
        $s = Login
        $dep = Api POST '/departments' @{ name = 'CI Finance' } -Session $s
        $dup = Invoke-WebRequest "$base/api/v1/departments" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -SkipHttpErrorCheck -Body (@{ name = 'ci finance' } | ConvertTo-Json)
        Assert ($dup.StatusCode -eq 400) "duplicate department name accepted: $($dup.StatusCode)"
        $assets = (Api GET '/assets?pageSize=5' -Session $s).items
        Assert (@($assets).Count -ge 1) 'no assets'
        $ids = @($assets | Select-Object -First 2 | ForEach-Object id)
        $r = Api POST '/assets/department' @{ assetIds = $ids; departmentId = $dep.id } -Session $s
        Assert ($r.updated -eq $ids.Count -and $r.department -eq 'CI Finance') "assign: $($r | ConvertTo-Json -Compress)"
        $f = Api GET "/assets?department=$($dep.id)" -Session $s
        Assert ($f.total -eq $ids.Count -and $f.items[0].department -eq 'CI Finance') "filter by department: $($f.total)"
        $o = Api GET '/dashboard/overview' -Session $s
        Assert (@($o.byDepartment | Where-Object { $_.key -eq 'CI Finance' -and $_.count -eq $ids.Count }).Count -eq 1) 'dashboard has no department chart data'
        $rep = Api POST '/reports/run' @{ dataset = 'assets'; definition = @{ columns = @('department', 'hostname'); filters = @(@{ column = 'department'; op = 'eq'; value = 'CI Finance' }) } } -Session $s
        Assert ($rep.total -eq $ids.Count) "report filtered by department: $($rep.total)"
        $lib = @(Api GET '/reports' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.system -and $_.name -eq 'Assets by department' }
        $run = Api POST '/reports/run' @{ dataset = 'assets'; definition = (Api GET "/reports/$($lib.id)" -Session $s).definition } -Session $s
        Assert (@($run.rows | Where-Object { $_[0] -eq 'CI Finance' }).Count -eq 1) 'built-in "Assets by department" lacks the department'
        Api PUT "/departments/$($dep.id)" @{ name = 'CI Finance & Accounts' } -Session $s | Out-Null
        Assert ((Api GET "/assets/$($ids[0])" -Session $s).department -eq 'CI Finance & Accounts') 'rename not shown on the asset'
        Api DELETE "/departments/$($dep.id)" -Session $s | Out-Null
        Assert ($null -eq (Api GET "/assets/$($ids[0])" -Session $s).departmentId) 'asset still assigned after the department was deleted'
        "$($ids.Count) asset(s) assigned, filtered, charted and reported; rename and delete OK; duplicate name refused"
    } | Out-Null

    Check 'T57' 'Change history: software installed, updated and uninstalled on this server is recorded and shown device-wise' {
        $s = Login
        $me = (Api GET "/assets?q=$env:COMPUTERNAME&pageSize=10" -Session $s).items | Where-Object { $_.inventoryStatus -eq 'success' } | Select-Object -First 1
        Assert $me "no inventoried asset for $env:COMPUTERNAME"
        $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ZentriqCiChangeTest'
        $script:collectNotes = @()
        function Collect-Wait {
            $before = (Api GET "/assets/$($me.id)" -Session $s).lastInventoryAt
            $c = Api POST '/assets/collect' @{ assetIds = @($me.id) } -Session $s
            $deadline = (Get-Date).AddMinutes(2)
            do { Start-Sleep 10; $now = (Api GET "/assets/$($me.id)" -Session $s).lastInventoryAt } while ($now -eq $before -and (Get-Date) -lt $deadline)
            if ($now -ne $before) { $script:collectNotes += 'collect-now'; return }
            # "Collect inventory now" did not reach this device (see its message); scan it directly with the T28 credential.
            $script:collectNotes += "scan (collect-now said: $($c.message))"
            Assert $script:ciCred 'no credential from T28'
            $scan = Api POST '/discovery/scans' @{ name = 'ci-changes'; scanType = 'single'; targetsText = $me.ip; credentialIds = @($script:ciCred) } -Session $s
            $deadline = (Get-Date).AddMinutes(5)
            do { Start-Sleep 5; $d = Api GET "/discovery/scans/$($scan.id)" -Session $s } while ($d.scan.status -in 'queued', 'running' -and (Get-Date) -lt $deadline)
            $now = (Api GET "/assets/$($me.id)" -Session $s).lastInventoryAt
            if ($now -eq $before) { throw "inventory of $($me.hostname) was not collected again (scan $($d.scan.status); collect-now: $($c.message))" }
        }
        function Find-Change([string] $change) {
            @((Api GET "/inventory/changes?assetId=$($me.id)&kind=software&days=1&pageSize=200" -Session $s).items | Where-Object { $_.item -eq 'Zentriq CI Change Test' -and $_.change -eq $change })
        }
        try {
            Remove-Item $key -Recurse -ErrorAction SilentlyContinue
            Collect-Wait   # baseline without the test application
            New-Item $key -Force | Out-Null
            New-ItemProperty $key -Name DisplayName -Value 'Zentriq CI Change Test' -Force | Out-Null
            New-ItemProperty $key -Name DisplayVersion -Value '1.0.0' -Force | Out-Null
            New-ItemProperty $key -Name Publisher -Value 'Zentriq CI' -Force | Out-Null
            Collect-Wait
            $i = Find-Change 'installed'
            Assert ($i.Count -eq 1 -and $i[0].newValue -eq '1.0.0') "installation not recorded: $((Api GET "/inventory/changes?assetId=$($me.id)&days=1" -Session $s).items | ConvertTo-Json -Compress -Depth 3)"
            Set-ItemProperty $key -Name DisplayVersion -Value '2.0.0'
            Collect-Wait
            $u = Find-Change 'updated'
            Assert ($u.Count -eq 1 -and $u[0].oldValue -eq '1.0.0' -and $u[0].newValue -eq '2.0.0') 'update not recorded'
            Remove-Item $key -Recurse
            Collect-Wait
            Assert ((Find-Change 'uninstalled').Count -eq 1) 'uninstallation not recorded'
        } finally { Remove-Item $key -Recurse -ErrorAction SilentlyContinue }
        $sum = Api GET '/inventory/changes/summary?days=1' -Session $s
        $row = @($sum.softwareDevices | Where-Object assetId -eq $me.id)
        Assert ($row.Count -eq 1 -and $row[0].changes -ge 3 -and $row[0].summary -match 'Zentriq CI Change Test') 'device-wise summary lacks the changes'
        $lib = @(Api GET '/reports' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.system -and $_.name -eq 'Software changes (30 days)' }
        $run = Api POST '/reports/run' @{ dataset = 'changes'; definition = (Api GET "/reports/$($lib.id)" -Session $s).definition } -Session $s
        Assert ($run.total -ge 3) "software changes report: $($run.total) row(s)"
        $hw = @((Api GET "/inventory/changes?assetId=$($me.id)&kind=hardware&days=1" -Session $s).items)
        "$($me.hostname): installed 1.0.0 -> updated to 2.0.0 -> uninstalled, all recorded; device-wise summary $($row[0].changes) change(s); report $($run.total) row(s); hardware changes during the test: $($hw.Count); collected by: $(($script:collectNotes | Select-Object -Unique) -join ' / ')"
    } | Out-Null

    Check 'T58' 'Company logo: upload (PNG), shown without sign-in, unsafe types refused, removal' {
        $s = Login
        $png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==')
        $svg = [Text.Encoding]::UTF8.GetBytes('<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>')
        $bad = Invoke-WebRequest "$base/api/v1/organization/logo" -Method Put -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/octet-stream' -Body $svg -SkipHttpErrorCheck
        Assert ($bad.StatusCode -eq 400) "SVG logo accepted: $($bad.StatusCode)"
        $ok = Invoke-WebRequest "$base/api/v1/organization/logo" -Method Put -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/octet-stream' -Body $png -SkipHttpErrorCheck
        Assert ($ok.StatusCode -eq 204) "upload returned $($ok.StatusCode)"
        $get = Invoke-WebRequest "$base/api/v1/organization/logo" -SkipHttpErrorCheck   # no session: the sign-in page shows it
        Assert ($get.StatusCode -eq 200 -and $get.Headers['Content-Type'] -match 'image/png' -and $get.RawContentStream.Length -eq $png.Length) "anonymous GET: $($get.StatusCode) $($get.Headers['Content-Type'])"
        Assert ((Api GET '/organization' -Session $s).logoUpdatedAt) 'logoUpdatedAt missing'
        Api DELETE '/organization/logo' -Session $s | Out-Null
        Assert ((Invoke-WebRequest "$base/api/v1/organization/logo" -SkipHttpErrorCheck).StatusCode -eq 404) 'logo still served after removal'
        $lic = Api GET '/licensing/status' -Session $s
        Assert (@($lic.usage | Where-Object workload -eq 'devices').Count -eq 1) 'licensing usage missing'
        "PNG stored and served anonymously ($($png.Length) bytes); SVG refused; removed"
    } | Out-Null

    # ---- Remote support (MeshCentral engine, Zentriq branded) --------------------------------------------------
    $remoteExe = Join-Path $OutDir 'ZentriqRemoteSupport.exe'
    function Remote-Wait([scriptblock] $Until, [int] $Minutes, [string] $What) {
        $deadline = (Get-Date).AddMinutes($Minutes)
        do { $v = & $Until; if ($v) { return $v }; Start-Sleep 10 } while ((Get-Date) -lt $deadline)
        throw "timed out after $Minutes min waiting for $What"
    }
    function Remote-Me($s) {
        Api POST '/remote/sync' @{} -Session $s | Out-Null
        @(Api GET "/remote/devices?q=$env:COMPUTERNAME&filter=installed" -Session $s | ForEach-Object { $_ }) | Where-Object { $_.nodeId -and $_.assetId -and $_.online } | Select-Object -First 1
    }

    $remoteOk = Check 'T59' 'Remote support: branded server running, installer, remote agent connects, technician session opens signed in' {
        $s = Login
        Assert ((Get-Service ZentriqRemote).Status -eq 'Running') 'service ZentriqRemote is not running'
        $h = Invoke-WebRequest 'https://localhost:8446/health.ashx' -SkipCertificateCheck -SkipHttpErrorCheck
        Assert ($h.StatusCode -eq 200) "health.ashx returned $($h.StatusCode)"
        $st = Api GET '/remote/status' -Session $s
        Assert ($st.installed -and $st.running) "remote status: installed=$($st.installed) running=$($st.running) $($st.message)"
        $login = Invoke-WebRequest 'https://localhost:8446/' -SkipCertificateCheck
        function Served-Cert([int] $port) {
            $tcp = [Net.Sockets.TcpClient]::new('localhost', $port)
            try {
                $ssl = [Net.Security.SslStream]::new($tcp.GetStream(), $false, { $true })
                $ssl.AuthenticateAsClient('localhost')
                [Security.Cryptography.X509Certificates.X509Certificate2]::new($ssl.RemoteCertificate).Thumbprint
            } finally { $tcp.Dispose() }
        }
        $consoleThumb = Served-Cert $HttpsPort; $remoteThumb = Served-Cert 8446
        Assert ($consoleThumb -eq $remoteThumb) "remote support serves certificate $remoteThumb, the console $consoleThumb"
        Assert ($login.Content -match 'Zentriq') 'remote support sign-in page is not Zentriq branded'
        $r = Invoke-WebRequest "$base/api/v1/remote/installer" -WebSession $s -OutFile $remoteExe -PassThru -SkipHttpErrorCheck
        Assert ($r.StatusCode -eq 200) "installer download returned $($r.StatusCode)"
        $bytes = [IO.File]::ReadAllBytes($remoteExe)
        Assert ($bytes.Length -gt 100KB -and $bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A) "installer is not a Windows executable ($($bytes.Length) bytes)"
        $vi = (Get-Item $remoteExe).VersionInfo
        Assert ("$($vi.ProductName) $($vi.FileDescription) $($vi.CompanyName)" -match 'Zentriq') "installer version info not branded: '$($vi.ProductName)' / '$($vi.FileDescription)' / '$($vi.CompanyName)'"
        $p = Start-Process $remoteExe -ArgumentList '-fullinstall' -Wait -PassThru
        Remote-Wait { Get-Service ZentriqRemoteSupport -ErrorAction SilentlyContinue | Where-Object Status -eq 'Running' } 4 'service ZentriqRemoteSupport' | Out-Null
        $me = Remote-Wait { Remote-Me $s } 5 "$env:COMPUTERNAME online in Remote Support"
        $ses = Api POST '/remote/sessions' @{ assetId = $me.assetId; view = 'desktop' } -Session $s
        Assert ($ses.url -match '[?&]login=' -and $ses.url -match 'gotonode=') "session url: $($ses.url)"
        $page = Invoke-WebRequest $ses.url -SkipCertificateCheck
        Assert ($page.Content -match 'MainDevDesktop') 'the session link did not open the signed-in device view'
        # The browser then opens the control channel from the page's origin. Opened by IP address, it must be accepted
        # (builds before 54: "Invalid origin in HTTP request").
        $ip = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' -and $_.AddressState -eq 'Preferred' } | ForEach-Object IPAddress)[0]
        $ses2 = Api POST '/remote/sessions' @{ assetId = $me.assetId; view = 'desktop' } -Session $s
        $byIp = $ses2.url -replace '//[^/:]+:', "//${ip}:"
        $null = Invoke-WebRequest $byIp -SkipCertificateCheck -SessionVariable rs
        function First-ControlMessage([string] $origin) {
            $ws = [Net.WebSockets.ClientWebSocket]::new()
            $ws.Options.Cookies = $rs.Cookies
            $ws.Options.SetRequestHeader('Origin', $origin)
            # No certificate callback: a PowerShell script block cannot run on the thread pool, and none is needed — port
            # 8446 serves the console certificate from the Zentriq Local CA, which is trusted here and covers this address.
            try { $ws.ConnectAsync([Uri]"wss://${ip}:8446/control.ashx", [Threading.CancellationToken]::None).GetAwaiter().GetResult() }
            catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; return "(connect failed: $($e.Message))" }
            $buf = [byte[]]::new(65536)
            $seg = [ArraySegment[byte]]::new($buf)
            $t = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None)
            if (-not $t.Wait(20000)) { return '(no message)' }
            [Text.Encoding]::UTF8.GetString($buf, 0, $t.Result.Count)
        }
        $okMsg = First-ControlMessage "https://${ip}:8446"
        Assert ($okMsg -notmatch 'invalidorigin|^\(' ) "control channel opened by IP ${ip}: $okMsg"
        Assert ((First-ControlMessage 'https://evil.example') -match 'invalidorigin') 'a foreign origin was accepted'
        $forged = Invoke-WebRequest ($ses.url -replace 'login=[^&]+', 'login=AAAA') -SkipCertificateCheck -SkipHttpErrorCheck
        Assert ($forged.Content -notmatch 'MainDevDesktop') 'a forged sign-in link was accepted'
        $log = Api GET "/remote/sessions?assetId=$($me.assetId)" -Session $s
        Assert (@($log.items | Where-Object { $_.view -eq 'desktop' }).Count -ge 1) 'session not in the session history'
        "engine healthy, same certificate as the console ($($consoleThumb.Substring(0,8))...); branded installer $([math]::Round($bytes.Length/1MB,1)) MB ('$($vi.FileDescription)', exit $($p.ExitCode)); $($me.name) online as $($me.os); signed-in desktop link opened; control channel by IP $ip accepted, foreign origin refused; forged link refused"
    }

    Check 'T60' 'Remote support via the Zentriq agent: remove and install the remote agent from the console' {
        Assert $remoteOk 'T59 did not pass'
        Assert (Test-Path $agentMsi) 'agent package from T26 not available'
        $s = Login
        $tok = Api POST '/agents/enrollment-tokens' @{ name = 'ci-remote'; validDays = 1; maxUses = 1; serverUrl = 'https://localhost:8444' } -Session $s
        $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$agentMsi`"", '/qn', "SERVERURL=https://localhost:8444", "ENROLLMENTCODE=$($tok.code)", 'CHECKININTERVAL=2', 'INVENTORYINTERVAL=30', '/l*v', "`"$(Join-Path $OutDir 'agent-install-remote.log')`"" -Wait -PassThru
        Assert ($p.ExitCode -eq 0) "agent MSI exit $($p.ExitCode)"
        try {
            $dev = Remote-Wait { @(Api GET "/remote/devices?q=$env:COMPUTERNAME" -Session $s | ForEach-Object { $_ }) | Where-Object { $_.assetId -and $_.canInstall } | Select-Object -First 1 } 6 'the Zentriq agent to report the remote-support capability'
            $r = Api POST '/remote/deploy' @{ assetIds = @($dev.assetId); operation = 'uninstall' } -Session $s
            Assert ($r.queued -eq 1) "uninstall not queued: $($r.message)"
            Remote-Wait { -not (Get-Service ZentriqRemoteSupport -ErrorAction SilentlyContinue) } 8 'the remote agent to be removed' | Out-Null
            $act = Remote-Wait { @((Api GET '/activity?kind=remote-support' -Session $s).items | Where-Object { $_.status -in 'succeeded', 'failed' }) | Select-Object -First 1 } 3 'the removal result'
            Assert ($act.status -eq 'succeeded') "removal: $($act.status) $($act.detail)"
            $r = Api POST '/remote/deploy' @{ assetIds = @($dev.assetId); operation = 'install' } -Session $s
            Assert ($r.queued -eq 1) "install not queued: $($r.message)"
            Remote-Wait { Get-Service ZentriqRemoteSupport -ErrorAction SilentlyContinue | Where-Object Status -eq 'Running' } 8 'the remote agent to be installed' | Out-Null
            $me = Remote-Wait { Remote-Me $s } 5 "$env:COMPUTERNAME back online in Remote Support"
            # ---- T85: the remote support agent is tamper-protected like the Zentriq Agent ----
            Check 'T85' 'Remote support agent tamper protection: with tamper protection on, administrators cannot stop, disable or reconfigure ZentriqRemoteSupport; the console shows it protected; lifted again when protection is switched off' {
                $hardSddl = [regex]::Escape('(A;;CCLCSWRPLOCRRC;;;BA)')
                Api PUT '/agents/settings' @{ checkinMinutes = 30; inventoryMinutes = 180; uninstallProtection = $true; autoUpdate = $false; autoUpdateMax = 25 } -Session $s | Out-Null
                try {
                    Remote-Wait { if (((& sc.exe sdshow ZentriqRemoteSupport) -join '') -match $hardSddl) { $true } } 6 'the remote support service to be protected' | Out-Null
                    $out = (& sc.exe stop ZentriqRemoteSupport 2>&1) -join ' '
                    $stopExit = $LASTEXITCODE
                    Start-Sleep 2
                    Assert ($stopExit -ne 0 -and (Get-Service ZentriqRemoteSupport).Status -eq 'Running') "an administrator could stop the protected remote support agent: $out"
                    $cfg = (& sc.exe config ZentriqRemoteSupport start= disabled 2>&1) -join ' '
                    Assert ($LASTEXITCODE -ne 0) "an administrator could disable the protected remote support agent: $cfg"
                    $del = (& sc.exe delete ZentriqRemoteSupport 2>&1) -join ' '
                    Assert ($LASTEXITCODE -ne 0 -and (Get-Service ZentriqRemoteSupport -ErrorAction SilentlyContinue)) "an administrator could delete the protected service: $del"
                    $dev = Remote-Wait { @(Api GET '/deployment/devices' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.remoteProtection -eq 'on' } | Select-Object -First 1 } 5 'the console to show the remote support agent protected'
                    "stop refused (exit $stopExit); disable and delete refused; console: protected on $($dev.hostname)"
                } finally {
                    Api PUT '/agents/settings' @{ checkinMinutes = 30; inventoryMinutes = 180; uninstallProtection = $false; autoUpdate = $false; autoUpdateMax = 25 } -Session $s | Out-Null
                    Remote-Wait { if (-not (((& sc.exe sdshow ZentriqRemoteSupport) -join '') -match $hardSddl)) { $true } } 6 'the remote support protection to be lifted' | Out-Null
                }
            } | Out-Null
            $done = @((Api GET '/activity?kind=remote-support' -Session $s).items | Where-Object { $_.status -eq 'succeeded' }).Count
            "removed and reinstalled through the Zentriq agent; $($me.name) online again; $done successful remote-support action(s) in the Activity log"
        } finally {
            if (Test-Path $remoteExe) { Start-Process $remoteExe -ArgumentList '-fulluninstall' -Wait -ErrorAction SilentlyContinue | Out-Null }
            @(Api GET '/agents' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.status -eq 'active' } | ForEach-Object { Api POST "/agents/$($_.id)/revoke" -Session $s | Out-Null }
            @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Zentriq Agent' }) |
                ForEach-Object { Start-Process msiexec.exe -ArgumentList '/x', $_.PSChildName, '/qn' -Wait | Out-Null }
        }
    } | Out-Null

    Check 'T61' 'Server certificate follows the server: local CA, all IP addresses, automatic re-issue with the same key, remote support follows' {
        function Served([int] $port) {
            # The remote support engine restarts when the certificate changes: a refused connection means "not yet".
            try { $tcp = [Net.Sockets.TcpClient]::new('127.0.0.1', $port) } catch { return [pscustomobject]@{ Thumbprint = '(not answering)' } }
            try {
                $ssl = [Net.Security.SslStream]::new($tcp.GetStream(), $false, { $true })
                $ssl.AuthenticateAsClient('localhost')
                [Security.Cryptography.X509Certificates.X509Certificate2]::new($ssl.RemoteCertificate)
            } catch { [pscustomobject]@{ Thumbprint = '(not answering)' } } finally { $tcp.Dispose() }
        }
        function Sans($c) { ($c.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' }).Format($false) }
        $caResp = Invoke-WebRequest "$base/api/v1/trust/ca.cer"   # no sign-in needed
        $ca = [Security.Cryptography.X509Certificates.X509Certificate2]::new([byte[]]$caResp.Content)
        Assert ($ca.Subject -like 'CN=Zentriq Local CA*') "CA subject: $($ca.Subject)"
        Assert (Test-Path "Cert:\LocalMachine\Root\$($ca.Thumbprint)") 'local CA is not trusted on the server'
        $before = Served $HttpsPort
        Assert ($before.Issuer -eq $ca.Subject) "console certificate issued by $($before.Issuer)"
        $ips = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' -and $_.AddressState -eq 'Preferred' } | ForEach-Object IPAddress)
        $san = Sans $before
        foreach ($ip in $ips) {
            Assert ($san -match [regex]::Escape($ip)) "certificate does not cover $ip ($san)"
            $r = Invoke-WebRequest "https://${ip}$(if ($HttpsPort -ne 443) { ":$HttpsPort" })/" -SkipHttpErrorCheck   # no -SkipCertificateCheck: must validate by IP
            Assert ($r.StatusCode -eq 200) "https://$ip returned $($r.StatusCode)"
        }
        $s = Login
        $trust = Api GET '/trust' -Session $s
        Assert ($trust.mode -eq 'local-ca') "trust mode $($trust.mode)"

        # The server gets a new name: the API re-issues the certificate by itself (same key: agent pins stay valid) and
        # remote support switches to it.
        $cfgPath = Join-Path $dataDir 'Config\zentriq.json'
        $original = Get-Content $cfgPath -Raw
        $cfg = $original | ConvertFrom-Json
        $cfg.Zentriq.Https.HostName = 'zq-renamed.ci.test'
        $cfg | ConvertTo-Json -Depth 20 | Set-Content $cfgPath -Encoding UTF8
        try {
            Restart-Service ZentriqApi -Force
            Wait-Ready | Out-Null
            $deadline = (Get-Date).AddMinutes(2)
            do { Start-Sleep 5; $after = Served $HttpsPort } while ((Sans $after) -notmatch 'zq-renamed\.ci\.test' -and (Get-Date) -lt $deadline)
            Assert ((Sans $after) -match 'zq-renamed\.ci\.test') "not re-issued for the new name: $(Sans $after)"
            Assert ($after.Thumbprint -ne $before.Thumbprint -and $after.Issuer -eq $ca.Subject) 'new certificate not from the local CA'
            Assert ([Convert]::ToBase64String($after.PublicKey.EncodedKeyValue.RawData) -eq [Convert]::ToBase64String($before.PublicKey.EncodedKeyValue.RawData)) 'the key changed (agents would reject the server)'
            $deadline = (Get-Date).AddMinutes(4)
            do { Start-Sleep 10; $remote = Served 8446 } while ($remote.Thumbprint -ne $after.Thumbprint -and (Get-Date) -lt $deadline)
            Assert ($remote.Thumbprint -eq $after.Thumbprint) "remote support still serves $($remote.Thumbprint)"
            $agentOk = Invoke-WebRequest "https://localhost:8444/api/agent/v1/checkin" -Method Post -Body '{}' -ContentType 'application/json' -SkipHttpErrorCheck -SkipCertificateCheck
            Assert ($agentOk.StatusCode -in 400, 401, 403) "agent port after re-issue: $($agentOk.StatusCode)"
            "CA $($ca.Thumbprint.Substring(0,8))... trusted; covers $($ips -join ', ') (validated by IP); new name -> re-issued $($after.Thumbprint.Substring(0,8))... with the same key in place; remote support switched to it"
        } finally {
            Set-Content $cfgPath $original -Encoding UTF8 -NoNewline
            Restart-Service ZentriqApi -Force
            Wait-Ready | Out-Null
        }
    } | Out-Null

    Check 'T62' 'Devices without an agent: recognised from their web interface; add, device type, duplicates merged (newest kept), delete' {
        $s = Login
        # The IPv4 address on the default route (Get-NetIPConfiguration throws on runners with duplicate adapter entries).
        $route = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object { $_.RouteMetric + $_.InterfaceMetric } | Select-Object -First 1
        $ip = (Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $route.ifIndex -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1).IPAddress
        # A printer's web page on port 8080 (what a network printer shows before sign-in).
        $web = Start-Job {
            $l = [Net.HttpListener]::new(); $l.Prefixes.Add('http://+:8080/'); $l.Start()
            $deadline = (Get-Date).AddMinutes(8)
            while ((Get-Date) -lt $deadline) {
                $t = $l.GetContextAsync()
                while (-not $t.Wait(1000)) { if ((Get-Date) -ge $deadline) { break } }
                if (-not $t.IsCompleted) { break }
                $b = [Text.Encoding]::UTF8.GetBytes('<html><head><title>HP LaserJet Pro M404dn</title></head><body>HP Embedded Web Server</body></html>')
                $t.Result.Response.ContentType = 'text/html'; $t.Result.Response.OutputStream.Write($b, 0, $b.Length); $t.Result.Response.Close()
            }
            $l.Stop()
        }
        try {
            Start-Sleep 3
            $scan = Api POST '/discovery/scans' @{ name = 'ci-recognise'; scanType = 'single'; targetsText = $ip; snmpPublic = $true } -Session $s
            $deadline = (Get-Date).AddMinutes(4)
            do { Start-Sleep 4; $d = Api GET "/discovery/scans/$($scan.id)" -Session $s } while ($d.scan.status -in 'queued', 'running' -and (Get-Date) -lt $deadline)
            $res = (Api GET "/discovery/scans/$($scan.id)/results" -Session $s).items | Select-Object -First 1
            Assert ($res.detail -match 'Recognised as Printer \(HP HP LaserJet Pro M404dn\)|Recognised as Printer \(HP') "scan result: $($res.status) $($res.detail)"
            Assert (@($res.openPorts) -contains 8080) "port 8080 not probed: $(@($res.openPorts) -join ',')"
            $me = Api GET "/assets/$($res.assetId)" -Session $s
            Assert ($me.deviceType -ne 'printer') "an inventoried computer was re-typed by a web page: $($me.deviceType)"
        } finally { Stop-Job $web -ErrorAction SilentlyContinue; Remove-Job $web -Force -ErrorAction SilentlyContinue }

        # Manual add with a device type, then a second record of the same device.
        $a = Api POST '/assets' @{ hostname = 'CI-PRINTER-1'; ip = '10.250.0.9'; deviceType = 'mfp'; macAddress = '00-1B-2C-3D-4E-5F'; manufacturer = 'Canon'; model = 'iR-ADV C3826'; criticality = 'Low' } -Session $s
        Assert ($a.deviceType -eq 'mfp' -and $a.deviceTypeManual -and $a.category -eq 'printer') "added: $($a.deviceType) manual=$($a.deviceTypeManual) category=$($a.category)"
        Start-Sleep 2
        $b = Api POST '/assets' @{ hostname = 'ci-printer-1'; ip = '10.250.0.10'; deviceType = 'printer' } -Session $s
        $dups = Api GET '/assets/duplicates' -Session $s
        $g = @($dups.groups | Where-Object { @($_.keep.id) + @($_.merge | ForEach-Object id) -contains $a.id })
        Assert ($g.Count -eq 1 -and (@($g[0].merge).Count -eq 1)) "duplicate group not found: $($dups | ConvertTo-Json -Depth 5 -Compress)"
        $m = Api POST '/assets/duplicates/merge' @{ keepIds = @($g[0].keep.id) } -Session $s
        Assert ($m.merged -eq 1) "merge: $($m.message)"
        $kept = Api GET "/assets/$($g[0].keep.id)" -Session $s
        $gone = Api GET "/assets/$($g[0].merge[0].id)" -Session $s
        Assert ($kept.status -eq 'active' -and $gone.status -eq 'duplicate') "after merge: kept $($kept.status), other $($gone.status)"
        Assert ($kept.ip -eq '10.250.0.10') "kept record does not have the newest address: $($kept.ip)"

        # Device type: manual, then automatic again.
        $t = Api PUT "/assets/$($kept.id)/device-type" @{ deviceType = 'scanner' } -Session $s
        Assert ($t.deviceType -eq 'scanner' -and $t.deviceTypeManual -and $t.category -eq 'printer') "manual type: $($t.deviceType)"
        $t = Api PUT "/assets/$($kept.id)/device-type" @{ deviceType = $null } -Session $s
        Assert (-not $t.deviceTypeManual) 'automatic type not restored'
        $types = @(Api GET '/assets/device-types' -Session $s | ForEach-Object { $_ })
        Assert ($types.Count -ge 20 -and ($types | Where-Object key -eq 'firewall').category -eq 'network_device') "device types: $($types.Count)"

        # Automatic clean-up setting and deletion.
        Api PUT '/assets/duplicates/settings' @{ auto = $false } -Session $s | Out-Null
        Assert (-not (Api GET '/assets/duplicates' -Session $s).auto) 'automatic merge not switched off'
        Api PUT '/assets/duplicates/settings' @{ auto = $true } -Session $s | Out-Null
        $del = Api POST '/assets/delete' @{ ids = @($kept.id) } -Session $s
        Assert ($del.deleted -eq 2) "deleted $($del.deleted) record(s) (kept + merged)"
        $r = Invoke-WebRequest "$base/api/v1/assets/$($kept.id)" -WebSession $s -SkipHttpErrorCheck
        Assert ($r.StatusCode -eq 404) "deleted device still readable ($($r.StatusCode))"
        "web page on 8080 recognised ($($res.detail -replace '.*(Recognised as [^:]+).*', '$1')); inventoried computer kept its type ($($me.deviceType)); added mfp, duplicate merged keeping $($kept.ip); type manual -> automatic; deleted 2 record(s)"
    } | Out-Null

    Check 'T63' 'Network inventory: summary, devices, port search, topology, VLANs, unmanaged switches, firmware, device network view, reports' {
        $s = Login
        $sum = Api GET '/network/summary' -Session $s
        foreach ($k in 'devices', 'byType', 'byManagement', 'portsTotal', 'unmanagedSuspected', 'switchesManaged', 'vlans', 'links', 'speeds') {
            Assert ($null -ne $sum.PSObject.Properties[$k]) "summary has no '$k'"
        }
        $dev = @(Api GET '/network/devices' -Session $s | ForEach-Object { $_ })
        $sw = @(Api GET '/network/devices?type=switch&management=snmp' -Session $s | ForEach-Object { $_ })
        $ports = @(Api GET '/network/ports?q=00:1a:2b&filter=up' -Session $s | ForEach-Object { $_ })
        $links = @(Api GET '/network/neighbors' -Session $s | ForEach-Object { $_ })
        $vl = @(Api GET '/network/vlans' -Session $s | ForEach-Object { $_ })
        $un = Api GET '/network/unmanaged' -Session $s
        Assert ($null -ne $un.PSObject.Properties['inferred'] -and $null -ne $un.PSObject.Properties['notManageable']) 'unmanaged: missing sections'
        $fw = @(Api GET '/network/firmware' -Session $s | ForEach-Object { $_ })
        # The network view of any device (here the first asset): which switch port it is connected to.
        $any = (Api GET '/assets?page=1&pageSize=1' -Session $s).items | Select-Object -First 1
        if ($any) {
            $nv = Api GET "/assets/$($any.id)/network" -Session $s
            Assert ($null -ne $nv.PSObject.Properties['connectedTo'] -and $null -ne $nv.PSObject.Properties['interfaces']) 'device network view incomplete'
        }
        # Built-in network reports run.
        $lib = @(Api GET '/reports' -Session $s | ForEach-Object { $_ } | Where-Object { $_.system -and $_.dataset -like 'network_*' })
        Assert ($lib.Count -ge 8) "built-in network reports: $($lib.Count)"
        foreach ($rep in $lib) {
            $r = Api GET "/reports/$($rep.id)/data" -Session $s
            Assert ($r.columns.Count -ge 1) "report '$($rep.name)' returned no columns"
        }
        # Scans accept the follow-neighbours option.
        # The IPv4 address on the default route (Get-NetIPConfiguration throws on runners with duplicate adapter entries).
        $route = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object { $_.RouteMetric + $_.InterfaceMetric } | Select-Object -First 1
        $ip = (Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $route.ifIndex -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1).IPAddress
        $scan = Api POST '/discovery/scans' @{ name = 'ci-follow-neighbours'; scanType = 'single'; targetsText = $ip; followNeighbors = $true } -Session $s
        $deadline = (Get-Date).AddMinutes(3)
        do { Start-Sleep 3; $d = Api GET "/discovery/scans/$($scan.id)" -Session $s } while ($d.scan.status -in 'queued', 'running' -and (Get-Date) -lt $deadline)
        Assert ($d.scan.status -in 'completed', 'completed_errors') "scan with follow neighbours: $($d.scan.status) $($d.scan.error)"
        "summary: $($sum.devices) network device(s), $($sum.portsTotal) port(s); $($dev.Count) device(s), $($sw.Count) managed switch(es), $($ports.Count) port match(es), $($links.Count) link(s), $($vl.Count) VLAN(s), $(@($un.inferred).Count) unmanaged, $($fw.Count) firmware row(s); $($lib.Count) network reports ran; follow-neighbours scan $($d.scan.status)"
    } | Out-Null

    Check 'T64' 'Non-IT assets: categories, add, barcode lookup, Excel form, bulk upload (check and apply), bulk change, export, reports, delete' {
        $s = Login
        $cats = @(Api GET '/nonit/categories' -Session $s | ForEach-Object { $_ })
        Assert ($cats.Count -ge 50) "built-in categories: $($cats.Count)"
        $chair = $cats | Where-Object name -eq 'Office chair' | Select-Object -First 1
        $custom = Api POST '/nonit/categories' @{ name = 'CI podium'; group = 'Furniture' } -Session $s
        $field = Api POST '/nonit/fields' @{ label = 'Cost centre'; type = 'text' } -Session $s
        $a = Api POST '/nonit/assets' @{ name = 'CI ergonomic chair'; categoryId = $chair.id; barcode = 'CI-BC-0001'; purchaseCost = 12500; quantity = 2; purchaseDate = '2025-04-01'; usefulLifeYears = 5; warrantyUntil = (Get-Date).AddDays(40).ToString('yyyy-MM-dd'); custom = @{ cost_centre = 'CC-42' } } -Session $s
        $got = Api GET "/nonit/lookup?code=CI-BC-0001" -Session $s
        Assert ($got.id -eq $a.id -and $got.assetTag -match '^NIT-\d{6}$' -and $got.custom.cost_centre -eq 'CC-42') "lookup: $($got | ConvertTo-Json -Compress -Depth 3)"
        Assert ($got.currentValue -gt 0 -and $got.currentValue -lt 12500) "current value $($got.currentValue)"
        # Excel form.
        $t = Invoke-WebRequest "$base/api/v1/nonit/template" -WebSession $s
        $bytes = $t.RawContentStream.ToArray()
        Assert ($bytes[0] -eq 0x50 -and $bytes[1] -eq 0x4B) 'form is not an xlsx package'
        [IO.File]::WriteAllBytes((Join-Path $OutDir 'ci-nonit-form.xlsx'), $bytes)
        # Bulk upload (CSV with the form headings): check, then apply.
        $csv = "Asset tag,Name *,Category *,Status,Purchase date,Purchase cost,Location,Cost centre`r`nCI-FUR-1,CI desk,Desk / workstation,In use,31/03/2025,`"8,000`",Floor 1,CC-1`r`n,CI AC,Air conditioner,In store,,45000,Store room,`r`n,CI golf cart,CI golf cart,,,,,`r`n"
        $file = Join-Path $OutDir 'ci-nonit.csv'; [IO.File]::WriteAllText($file, $csv)
        $up = { param($q) Invoke-RestMethod "$base/api/v1/nonit/import?$q" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/octet-stream' -InFile $file }
        $check = & $up 'dryRun=true'
        Assert (-not $check.ok -and @($check.errors | Where-Object { $_.row -eq 4 }).Count -ge 1) "check should report the unknown category in row 4: $($check | ConvertTo-Json -Compress -Depth 4)"
        $check2 = & $up 'dryRun=true&createCategories=true'
        Assert ($check2.ok -and $check2.created -eq 3) "check with new categories: $($check2 | ConvertTo-Json -Compress -Depth 4)"
        $applied = & $up 'dryRun=false&createCategories=true'
        Assert ($applied.ok -and -not $applied.dryRun -and $applied.created -eq 3) "upload: $($applied | ConvertTo-Json -Compress -Depth 4)"
        $list = Api GET '/nonit/assets?q=CI&pageSize=50' -Session $s
        Assert ($list.total -ge 4) "assets after upload: $($list.total)"
        $desk = $list.items | Where-Object assetTag -eq 'CI-FUR-1'
        Assert ($desk.purchaseCost -eq 8000 -and $desk.custom.cost_centre -eq 'CC-1') "desk: $($desk | ConvertTo-Json -Compress -Depth 3)"
        # Bulk change, export, summary, reports.
        $b = Api POST '/nonit/assets/bulk' @{ ids = @($a.id, $desk.id); status = 'under_maintenance'; location = 'Workshop' } -Session $s
        Assert ($b.updated -eq 2) "bulk change: $($b.updated)"
        $one = Api GET "/nonit/assets/$($a.id)" -Session $s
        Assert ($one.status -eq 'under_maintenance' -and @($one.history).Count -ge 2) "history: $(@($one.history).Count)"
        $x = Invoke-WebRequest "$base/api/v1/nonit/export?format=xlsx" -WebSession $s
        Assert ($x.RawContentStream.ToArray()[0] -eq 0x50) 'export is not xlsx'
        $sum = Api GET '/nonit/summary' -Session $s
        Assert ($sum.total -ge 4 -and $sum.warranty90 -ge 1) "summary: $($sum | ConvertTo-Json -Compress -Depth 2)"
        $lib = @(Api GET '/reports' -Session $s | ForEach-Object { $_ } | Where-Object { $_.system -and $_.dataset -eq 'nonit_assets' })
        Assert ($lib.Count -ge 7) "built-in non-IT reports: $($lib.Count)"
        foreach ($rep in $lib) { $r = Api GET "/reports/$($rep.id)/data" -Session $s; Assert ($r.columns.Count -ge 1) "report '$($rep.name)' returned no columns" }
        # Delete.
        $ids = @($list.items | ForEach-Object id) + @($a.id) | Select-Object -Unique
        $d = Api POST '/nonit/assets/delete' @{ ids = $ids } -Session $s
        Assert ($d.deleted -ge 4) "deleted $($d.deleted)"
        Invoke-RestMethod "$base/api/v1/nonit/fields/$($field.id)" -Method Delete -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } | Out-Null
        "categories $($cats.Count) (+1 custom); lookup by barcode OK; form $($bytes.Length) bytes; upload checked (unknown category reported) and applied (3 new, 1 new category); bulk change + history; export; $($lib.Count) reports ran; $($d.deleted) deleted"
    } | Out-Null

    Check 'T65' 'Remote quick support: session link, run-only support app joins its own consent group, technician opens it, ending removes it' {
        Assert $remoteOk 'T59 did not pass'
        $s = Login
        # Without a checked public address, links for people outside the network are refused (409); on the LAN they work.
        $noInet = Invoke-WebRequest "$base/api/v1/remote/quick" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body '{"customer":"x","hours":1}' -SkipHttpErrorCheck
        Assert ($noInet.StatusCode -eq 409) "internet link without a public address returned $($noInet.StatusCode)"
        $priv = Invoke-WebRequest "$base/api/v1/remote/internet" -Method Post -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body '{"host":"192.168.1.4","port":8446,"save":true}' -SkipHttpErrorCheck
        Assert ($priv.StatusCode -eq 400) "a private address was accepted as internet address ($($priv.StatusCode))"
        $created = Api POST '/remote/quick' @{ customer = 'CI user'; note = 'acceptance'; hours = 1; lan = $true } -Session $s
        $q = $created.session
        Assert ($q.code -match '^\d{9}$' -and $q.inviteUrl -match '/agentinvite\?c=' -and $q.status -eq 'waiting') "session: $($q | ConvertTo-Json -Compress)"
        Assert ($created.invitation.text -match [regex]::Escape($q.inviteUrl)) 'the invitation text does not contain the link'
        $page = Invoke-WebRequest $q.inviteUrl -SkipCertificateCheck
        Assert ($page.Content -match "parseInt\('1'\)") 'the invitation page does not offer the run-only app'
        $visible = [regex]::Replace($page.Content, '<script[\s\S]*?</script>', '')
        Assert ($visible -notmatch 'MeshCentral' -and $page.Content -notmatch 'Android') 'the invitation page is not Zentriq branded'
        $mid = [regex]::Match($page.Content, "var meshid = '([^']+)'").Groups[1].Value
        Assert $mid 'no device group in the invitation page'
        $origin = ([Uri]$q.inviteUrl).GetLeftPart([UriPartial]::Authority)
        $exe = Join-Path $OutDir 'ZentriqQuickSupport.exe'
        Invoke-WebRequest "$origin/meshagents?id=4&meshid=$mid&installflags=1" -OutFile $exe -SkipCertificateCheck
        Assert ((Get-Item $exe).Length -gt 100000) 'support app download is too small'
        $proc = Start-Process $exe -ArgumentList 'connect' -PassThru -WindowStyle Hidden
        try {
            $joined = Remote-Wait { @((Api GET '/remote/quick' -Session $s).items | Where-Object { $_.id -eq $q.id -and $_.status -eq 'connected' -and $_.online }) | Select-Object -First 1 } 4 'the support app to join the session'
            $open = Api POST "/remote/quick/$($q.id)/open" @{ view = 'desktop' } -Session $s
            Assert ($open.url -match '[?&]login=' -and $open.url -match 'gotonode=') "open: $($open.url)"
            Assert ((Invoke-WebRequest $open.url -SkipCertificateCheck).Content -match 'MainDevDesktop') 'the session link did not open the device'
            $hist = Api GET '/remote/sessions' -Session $s
            Assert (@($hist.items | Where-Object { $_.device -match [regex]::Escape(($q.code -replace '(\d{3})(\d{3})(\d{3})', '$1 $2 $3')) }).Count -ge 1) 'quick session not in the history'
        }
        finally {
            try { Api POST "/remote/quick/$($q.id)/end" @{} -Session $s | Out-Null } catch { }
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
        $after = @((Api GET '/remote/quick' -Session $s).items | Where-Object id -eq $q.id)[0]
        Assert ($after.status -eq 'ended') "after end: $($after.status)"
        $link = Api POST '/remote/install-link' @{ hours = 1 } -Session $s
        Assert ((Invoke-WebRequest $link.url -SkipCertificateCheck).Content -match "parseInt\('2'\)") 'install link page'
        "session $($q.code): invitation page run-only, app joined as $($joined.deviceName) ($($joined.deviceOs)), desktop link opened, logged; ended and its group removed; install link OK"
    } | Out-Null

    Check 'T66' 'Non-IT groups and departments: add a group, a category in it, rename the group, department with non-IT count, clean up' {
        $s = Login
        $g = Api POST '/nonit/groups' @{ name = 'CI lab equipment' } -Session $s
        $c = Api POST '/nonit/categories' @{ name = 'CI microscope'; group = 'ci LAB equipment' } -Session $s
        Assert ($c.group -eq 'CI lab equipment') "category group: $($c.group)"
        $grp = @(Api GET '/nonit/groups' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $g.id
        Assert ($grp.categories -eq 1) "group categories: $($grp.categories)"
        Api PUT "/nonit/groups/$($g.id)" @{ name = 'CI laboratory' } -Session $s | Out-Null
        $cat = @(Api GET '/nonit/categories' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $c.id
        Assert ($cat.group -eq 'CI laboratory') "category after group rename: $($cat.group)"
        $dep = Api POST '/departments' @{ name = 'CI non-IT department' } -Session $s
        $a = Api POST '/nonit/assets' @{ name = 'CI scope'; categoryId = $c.id; departmentId = $dep.id } -Session $s
        $d = @((Api GET '/departments' -Session $s).items) | Where-Object id -eq $dep.id
        Assert ($d.nonItAssets -eq 1) "department non-IT count: $($d.nonItAssets)"
        Api POST '/nonit/assets/delete' @{ ids = @($a.id) } -Session $s | Out-Null
        Api DELETE "/nonit/categories/$($c.id)" -Session $s | Out-Null
        Api DELETE "/nonit/groups/$($g.id)" -Session $s | Out-Null
        Api DELETE "/departments/$($dep.id)" -Session $s | Out-Null
        Assert (-not (@(Api GET '/nonit/groups' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $g.id)) 'group not deleted'
        "group added, category joined it (case-insensitive), rename followed, department counted 1 non-IT asset; cleaned up"
    } | Out-Null

    Check 'T69' 'Locations and IT accessories: location on devices and hardware inventory; categories; receive, issue, return, transfer; no negative stock; holdings; reorder alert' {
        $s = Login
        function Raw([string] $Method, [string] $Path, $Body) {
            $p = @{ Uri = "$base/api/v1$Path"; Method = $Method; WebSession = $s; Headers = @{ 'X-Zentriq-Request' = '1' }; ContentType = 'application/json'; SkipHttpErrorCheck = $true }
            if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6) }
            $resp = Invoke-WebRequest @p
            # application/problem+json comes back as bytes: return the text as well.
            $text = if ($resp.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($resp.Content) } else { [string]$resp.Content }
            [pscustomobject]@{ StatusCode = [int]$resp.StatusCode; Content = $text }
        }
        $hq = Api POST '/locations' @{ name = 'CI head office store' } -Session $s
        $br = Api POST '/locations' @{ name = 'CI branch' } -Session $s
        $dup = Raw POST '/locations' @{ name = 'ci HEAD office store' }
        Assert ($dup.StatusCode -eq 400) "duplicate location name accepted ($($dup.StatusCode))"
        # Devices: location like the department, filterable in IT Asset and Hardware Inventory.
        $asset = (Api GET '/assets' -Session $s).items | Select-Object -First 1
        Assert $asset 'no device to assign a location to'
        $set = Api POST '/assets/location' @{ assetIds = @($asset.id); locationId = $hq.id } -Session $s
        Assert ($set.location -eq 'CI head office store') "assign location: $($set | ConvertTo-Json -Compress)"
        $listed = (Api GET "/assets?location=$($hq.id)" -Session $s).items
        Assert (@($listed).Count -eq 1 -and $listed[0].location -eq 'CI head office store') "asset list by location: $(@($listed).Count)"
        $hw = (Api GET "/inventory/hardware?location=$($hq.id)" -Session $s).items
        $hwNote = if (@($hw).Count -gt 0) { "hardware inventory filter: $(@($hw).Count) device(s), location $($hw[0].location)" } else { 'device has no hardware inventory' }

        # Accessory categories: built-in list, add, duplicate refused, rename moves accessories, delete only when unused.
        $cats = @(Api GET '/accessories/categories' -Session $s | ForEach-Object { $_ })
        Assert (@($cats | Where-Object { $_.name -eq 'Mouse' -and $_.builtIn }).Count -eq 1) "built-in categories: $(@($cats).Count)"
        $cat = Api POST '/accessories/categories' @{ name = 'CI stylus pen' } -Session $s
        Assert ((Raw POST '/accessories/categories' @{ name = 'ci STYLUS pen' }).StatusCode -eq 400) 'duplicate category accepted'
        $pen = Api POST '/accessories/items' @{ name = 'CI stylus'; category = 'CI stylus pen'; reorderLevel = 0 } -Session $s
        Api PUT "/accessories/categories/$($cat.id)" @{ name = 'CI pens' } -Session $s | Out-Null
        $penItem = @(Api GET '/accessories/items?all=true' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $pen.id
        Assert ($penItem.category -eq 'CI pens') "rename did not move the accessory: $($penItem.category)"
        Assert ((Raw DELETE "/accessories/categories/$($cat.id)").StatusCode -eq 409) 'a category in use was deleted'
        Assert ((Api GET '/accessories/summary' -Session $s).categories -contains 'CI pens') 'new category not offered in the accessory form'
        $tmp = Api POST '/accessories/categories' @{ name = 'CI temporary' } -Session $s
        Assert ((Raw DELETE "/accessories/categories/$($tmp.id)").StatusCode -eq 204) 'unused category not deleted'
        $catNote = "categories: built-in $(@($cats | Where-Object builtIn).Count), added, renamed (accessory moved), in-use delete refused, unused deleted"

        # Accessories.
        $item = Api POST '/accessories/items' @{ name = 'CI USB mouse'; category = 'Mouse'; reorderLevel = 5; unitCost = 400 } -Session $s
        $r = Api POST '/accessories/movements' @{ kind = 'receive'; locationId = $hq.id; vendor = 'CI vendor'; reference = 'INV-CI-1'; lines = @(@{ itemId = $item.id; quantity = 10 }) } -Session $s
        $r = Api POST '/accessories/movements' @{ kind = 'issue'; locationId = $hq.id; person = 'CI Person'; reason = 'New joiner'; lines = @(@{ itemId = $item.id; quantity = 6 }) } -Session $s
        $stock = @(Api GET "/accessories/stock?locationId=$($hq.id)" -Session $s | ForEach-Object { $_ }) | Where-Object itemId -eq $item.id
        Assert ($stock.quantity -eq 4 -and $stock.low) "stock after receive 10 / issue 6: $($stock | ConvertTo-Json -Compress)"
        $over = Raw POST '/accessories/movements' @{ kind = 'issue'; locationId = $hq.id; person = 'Someone else'; lines = @(@{ itemId = $item.id; quantity = 5 }) }
        Assert ($over.StatusCode -eq 400 -and $over.Content -match 'Only 4') "issue of more than the stock: $($over.StatusCode) $($over.Content)"
        $held = @(Api GET '/accessories/holdings?q=CI%20Person' -Session $s | ForEach-Object { $_ }) | Where-Object itemId -eq $item.id
        Assert ($held.quantity -eq 6) "holdings: $($held | ConvertTo-Json -Compress)"
        Api POST '/accessories/movements' @{ kind = 'return'; locationId = $hq.id; person = 'ci person'; condition = 'faulty'; lines = @(@{ itemId = $item.id; quantity = 1 }) } -Session $s | Out-Null
        Api POST '/accessories/movements' @{ kind = 'transfer'; locationId = $hq.id; toLocationId = $br.id; lines = @(@{ itemId = $item.id; quantity = 2 }) } -Session $s | Out-Null
        $all = @(Api GET '/accessories/stock' -Session $s | ForEach-Object { $_ }) | Where-Object itemId -eq $item.id
        $atHq = $all | Where-Object locationId -eq $hq.id; $atBr = $all | Where-Object locationId -eq $br.id
        Assert ($atHq.quantity -eq 2 -and $atHq.faulty -eq 1 -and $atBr.quantity -eq 2) "stock after return and transfer: $($all | ConvertTo-Json -Compress)"
        $moves = (Api GET "/accessories/movements?itemId=$($item.id)" -Session $s).total
        Assert ($moves -eq 4) "movements recorded: $moves"
        $sum = Api GET '/accessories/summary' -Session $s
        Assert ($sum.lowStock -ge 1) "low stock not counted: $($sum | ConvertTo-Json -Compress)"
        $del = Raw DELETE "/locations/$($hq.id)" $null
        Assert ($del.StatusCode -eq 409) "location with stock deleted ($($del.StatusCode))"
        $delItem = Raw DELETE "/accessories/items/$($item.id)" $null
        Assert ($delItem.StatusCode -eq 409) "accessory with history deleted ($($delItem.StatusCode))"
        Api POST '/assets/location' @{ assetIds = @($asset.id); locationId = $null } -Session $s | Out-Null
        "location assigned and filtered ($hwNote); received 10, issued 6 to CI Person (stock 4, below reorder 5), issue of 5 refused (Only 4), returned 1 faulty, transferred 2; $moves movements; location and accessory with history protected; $catNote"
    } | Out-Null

    Check 'T70' 'Manual hardware details: entered where not detected, linked to the fixed identities, searchable, detected values keep priority, removable' {
        $s = Login
        $agentDev = @(Api GET '/agents' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.status -eq 'active' -and $_.assetId } | Select-Object -First 1
        $id = if ($agentDev) { $agentDev.assetId } else { ((Api GET '/assets' -Session $s).items | Select-Object -First 1).id }
        Assert $id 'no device'
        $before = Api GET "/assets/$id" -Session $s
        $r = Api PUT "/assets/$id/hardware-identity" @{ manufacturer = 'CI Maker'; model = 'CI Model 70'; serialNumber = 'CI-MANUAL-SN-70' } -Session $s
        if ($agentDev) { Assert ($r.identities -ge 1) "not linked to any fixed identity: $($r | ConvertTo-Json -Compress)" }
        $a = Api GET "/assets/$id" -Session $s
        Assert ($a.manualSerial -eq 'CI-MANUAL-SN-70' -and $a.manualModel -eq 'CI Model 70') "manual values not stored: $($a | ConvertTo-Json -Compress -Depth 3)"
        # A value the device reports stays; a missing one is filled from what was entered.
        if ($before.serialNumber) { Assert ($a.serialNumber -eq $before.serialNumber -and 'serialNumber' -notin @($a.enteredManually)) "detected serial replaced: $($a.serialNumber)" }
        else { Assert ($a.serialNumber -eq 'CI-MANUAL-SN-70' -and 'serialNumber' -in @($a.enteredManually)) "missing serial not filled: $($a.serialNumber)" }
        $found = (Api GET '/assets?q=CI-MANUAL-SN-70' -Session $s).items
        Assert (@($found | Where-Object id -eq $id).Count -eq 1) 'device not found by the entered serial number'
        $long = Invoke-WebRequest -Uri "$base/api/v1/assets/$id/hardware-identity" -Method PUT -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -ContentType 'application/json' -Body (@{ model = ('x' * 201) } | ConvertTo-Json) -SkipHttpErrorCheck
        Assert ([int]$long.StatusCode -eq 400) "201-character model accepted ($([int]$long.StatusCode))"
        $hw = (Api GET '/inventory/hardware?q=CI-MANUAL-SN-70' -Session $s).items
        $hwNote = if (@($hw).Count -gt 0) { "hardware inventory shows it (manual: $(@($hw[0].enteredManually) -join ', '))" } else { 'serial detected, so hardware inventory keeps the detected one' }
        $c = Api PUT "/assets/$id/hardware-identity" @{ manufacturer = $null; model = $null; serialNumber = $null } -Session $s
        $a = Api GET "/assets/$id" -Session $s
        Assert (-not $a.manualSerial -and -not $a.manualModel -and -not $a.manualManufacturer) 'entered values not removed'
        "saved and linked to $($r.identities) identit(y/ies); $hwNote; 201 characters refused; removed"
    } | Out-Null

    Check 'T72' 'Asset lifecycle: add to stock with tags, details and depreciation, assign and return, contract, refresh, labels, retire and dispose with data sanitisation, report' {
        $s = Login
        function Raw([string] $Method, [string] $Path, $Body) {
            $p = @{ Uri = "$base/api/v1$Path"; Method = $Method; WebSession = $s; Headers = @{ 'X-Zentriq-Request' = '1' }; ContentType = 'application/json'; SkipHttpErrorCheck = $true }
            if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6) }
            $resp = Invoke-WebRequest @p
            $text = if ($resp.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($resp.Content) } else { [string]$resp.Content }
            [pscustomobject]@{ StatusCode = [int]$resp.StatusCode; Content = $text }
        }
        $meta = Api GET '/lifecycle/meta' -Session $s
        Assert (@($meta.stages).Count -eq 8 -and $meta.currency) "meta: $(@($meta.stages).Count) stages"
        $sn = 'CI-LC-' + (Get-Random -Maximum 999999)
        $add = Api POST '/lifecycle/stock' @{ items = @(@{ serial = $sn; manufacturer = 'CI'; model = 'CI Book 14'; deviceType = 'laptop' }); stage = 'in_stock'; status = 'new'; purchaseDate = '2024-01-15'; purchaseCost = 90000; vendor = 'CI Vendor'; poNumber = 'PO-CI-72'; warrantyEnd = (Get-Date).AddDays(20).ToString('yyyy-MM-dd') } -Session $s
        $item = @($add.created)[0]
        Assert ($item.tag -like 'LAP-*') "stock item tag: $($item.tag)"
        $dupe = Raw POST '/lifecycle/stock' @{ items = @(@{ serial = $sn; deviceType = 'laptop' }) }
        Assert ($dupe.StatusCode -eq 400) "same serial added twice ($($dupe.StatusCode))"
        $d = Api GET "/lifecycle/assets/$($item.id)" -Session $s
        Assert ($d.asset.stage -eq 'in_stock' -and $d.asset.awaitingFirstSighting -and $d.asset.bookValue -lt 90000 -and $d.asset.bookValue -gt 0) "stock item: $($d.asset | ConvertTo-Json -Compress -Depth 2)"
        Assert (@($d.schedule).Count -ge 4) "depreciation schedule rows: $(@($d.schedule).Count)"
        $summary = Api GET '/lifecycle/summary' -Session $s
        Assert ($summary.warranty30 -ge 1) "warranty ending not counted: $($summary | ConvertTo-Json -Compress -Depth 2)"
        # Billing ignores items in stock
        $usage = (Api GET '/licensing/status' -Session $s).usage

        # Assign the stock item, loan rules, people view, return.
        $bad = Raw POST "/lifecycle/assets/$($item.id)/stage" @{ stage = 'in_use'; status = 'loaned'; assignedTo = 'CI Person' }
        Assert ($bad.StatusCode -eq 400 -and $bad.Content -match 'loan') "loan without return date: $($bad.StatusCode) $($bad.Content)"
        Api POST "/lifecycle/assets/$($item.id)/stage" @{ stage = 'in_use'; status = 'assigned'; assignedTo = 'CI Person'; assignedEmail = 'ci.person@example.test' } -Session $s | Out-Null
        $people = @(Api GET '/lifecycle/people?q=CI%20Person' -Session $s | ForEach-Object { $_ })
        Assert (@($people | Where-Object person -eq 'CI Person').assets -ge 1) "people: $($people | ConvertTo-Json -Compress)"
        $hold = Api GET '/lifecycle/people/holdings?person=CI%20Person' -Session $s
        Assert (@($hold.assets | Where-Object id -eq $item.id).Count -eq 1) 'handover form does not list the asset'
        $jump = Raw POST "/lifecycle/assets/$($item.id)/stage" @{ stage = 'disposed'; status = 'recycled' }
        Assert ($jump.StatusCode -eq 400) "in use straight to disposed allowed ($($jump.StatusCode))"
        Api POST "/lifecycle/assets/$($item.id)/return" @{ stage = 'in_stock'; status = 'refurbished'; reason = 'CI return' } -Session $s | Out-Null

        # Details, contract, labels, refresh.
        $d = Api GET "/lifecycle/assets/$($item.id)" -Session $s
        Api PUT "/lifecycle/assets/$($item.id)" @{ assetTag = $d.asset.assetTag; acquisition = 'purchase'; purchaseDate = '2020-01-15'; purchaseCost = 90000; currency = 'INR'; vendor = 'CI Vendor'; warrantyEnd = '2023-01-15'; notes = 'CI'; rowVersion = $d.asset.rowVersion } -Session $s | Out-Null
        $stale = Raw PUT "/lifecycle/assets/$($item.id)" @{ purchaseCost = 1; rowVersion = $d.asset.rowVersion }
        Assert ($stale.StatusCode -eq 409) "stale update accepted ($($stale.StatusCode))"
        $c = Api POST '/lifecycle/contracts' @{ name = 'CI AMC'; kind = 'amc'; vendor = 'CI Vendor'; endDate = (Get-Date).AddDays(45).ToString('yyyy-MM-dd'); notifyDays = 60 } -Session $s
        $link = Api POST "/lifecycle/contracts/$($c.id)/assets" @{ add = @($item.id) } -Session $s
        Assert ($link.added -eq 1) 'asset not added to the contract'
        $refresh = Api GET '/lifecycle/refresh' -Session $s
        Assert (@($refresh.candidates | Where-Object id -eq $item.id).Count -eq 1) 'six-year-old laptop not a refresh candidate'
        $labels = @(Api GET "/lifecycle/labels?ids=$($item.id)" -Session $s | ForEach-Object { $_ })
        Assert ($labels.Count -eq 1 -and $labels[0].qrSvg -match '<svg') 'label without QR code'

        # Retire and dispose (NIST SP 800-88 sanitisation required).
        Api POST "/lifecycle/assets/$($item.id)/stage" @{ stage = 'retired'; status = 'pending_disposal'; reason = 'CI end of life' } -Session $s | Out-Null
        $noSan = Raw POST "/lifecycle/assets/$($item.id)/stage" @{ stage = 'disposed'; status = 'recycled'; disposedOn = (Get-Date).ToString('yyyy-MM-dd') }
        Assert ($noSan.StatusCode -eq 400 -and $noSan.Content -match 'sanit|800-88|data') "disposal without sanitisation: $($noSan.StatusCode) $($noSan.Content)"
        $today = (Get-Date).ToString('yyyy-MM-dd')
        Api POST "/lifecycle/assets/$($item.id)/stage" @{ stage = 'disposed'; status = 'recycled'; disposedOn = $today; sanitizationMethod = 'purge'; sanitizedBy = 'CI'; sanitizedOn = $today; certificateRef = 'COD-CI'; disposalVendor = 'CI Recycler' } -Session $s | Out-Null
        $d = Api GET "/lifecycle/assets/$($item.id)" -Session $s
        Assert ($d.asset.stage -eq 'disposed' -and $d.asset.assetStatus -eq 'retired') "after disposal: $($d.asset.stage) / $($d.asset.assetStatus)"
        $kinds = @($d.events | ForEach-Object kind)
        Assert ('disposed' -in $kinds -and 'assigned' -in $kinds -and 'returned' -in $kinds) "history: $($kinds -join ', ')"

        # Report dataset and the lifecycle tab of the server's own record.
        $r = Api POST '/reports/run' @{ dataset = 'asset_lifecycle'; definition = @{ columns = @('asset_tag', 'stage', 'disposal_method', 'sanitization_method', 'book_value'); filters = @(@{ column = 'stage'; op = 'eq'; value = 'Disposed' }) } } -Session $s
        Assert (@($r.rows).Count -ge 1) "disposal register empty: $($r | ConvertTo-Json -Compress -Depth 3)"
        $own = ((Api GET '/assets' -Session $s).items | Select-Object -First 1)
        $ownLc = Api GET "/lifecycle/assets/$($own.id)" -Session $s
        Assert ($ownLc.asset.stage -in 'in_use', 'missing') "server record lifecycle: $($ownLc.asset.stage)"
        Api DELETE "/lifecycle/contracts/$($c.id)" -Session $s | Out-Null
        "stock item $($item.tag) (book value below cost, schedule $(@($d.schedule).Count) years); duplicate serial refused; loan without date refused; assigned to CI Person, handover lists it; returned; stale edit 409; contract linked; refresh candidate; QR label; retired; disposal without sanitisation refused, disposed (purge, certificate); history $($kinds.Count) changes; report row(s) $(@($r.rows).Count)"
    } | Out-Null

    Check 'T73' 'Cloud & infrastructure: connection validation, write-only secret, failed sync recorded with a clear error, resources, findings, changes and reports' {
        $s = Login
        function Raw([string] $Method, [string] $Path, $Body) {
            $p = @{ Uri = "$base/api/v1$Path"; Method = $Method; WebSession = $s; Headers = @{ 'X-Zentriq-Request' = '1' }; ContentType = 'application/json'; SkipHttpErrorCheck = $true }
            if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6) }
            $resp = Invoke-WebRequest @p
            $text = if ($resp.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($resp.Content) } else { [string]$resp.Content }
            [pscustomobject]@{ StatusCode = [int]$resp.StatusCode; Content = $text }
        }
        $meta = Api GET '/cloud/meta' -Session $s
        Assert (@($meta.providers).Count -eq 9 -and @($meta.rules).Count -ge 10) "meta: $($meta | ConvertTo-Json -Compress -Depth 3)"
        $sum = Api GET '/cloud/summary' -Session $s
        Assert ($null -ne $sum.vms) "summary: $($sum | ConvertTo-Json -Compress -Depth 2)"
        # Validation
        $bad = Raw POST '/cloud/accounts' @{ name = 'CI bad'; provider = 'azure'; tenantId = 'not a tenant'; clientId = 'x'; secret = 'x' }
        Assert ($bad.StatusCode -eq 400 -and $bad.Content -match 'tenant') "bad Azure tenant: $($bad.StatusCode) $($bad.Content)"
        $bad = Raw POST '/cloud/accounts' @{ name = 'CI bad'; provider = 'aws'; accessKeyId = 'nope'; secret = 'x' }
        Assert ($bad.StatusCode -eq 400 -and $bad.Content -match 'AKIA') "bad AWS key: $($bad.StatusCode)"
        $bad = Raw POST '/cloud/accounts' @{ name = 'CI bad'; provider = 'hyperv'; hosts = @('hv01') }
        Assert ($bad.StatusCode -eq 400 -and $bad.Content -match 'credential') "Hyper-V without a Windows account: $($bad.StatusCode)"
        $bad = Raw POST '/cloud/accounts' @{ name = 'CI bad'; provider = 'gcp'; secret = '{"type":"authorized_user"}' }
        Assert ($bad.StatusCode -eq 400 -and $bad.Content -match 'JSON key') "Google Cloud with a non service-account key: $($bad.StatusCode)"
        $bad = Raw POST '/cloud/accounts' @{ name = 'CI bad'; provider = 'proxmox'; host = 'pve01'; username = 'root'; secret = 'x' }
        Assert ($bad.StatusCode -eq 400 -and $bad.Content -match 'token') "Proxmox without a token ID: $($bad.StatusCode)"
        # An unreachable Proxmox node fails cleanly too.
        $px = Api POST '/cloud/accounts' @{ name = 'CI Proxmox ' + (Get-Random -Maximum 99999); provider = 'proxmox'; host = '127.0.0.1'; port = 9; username = 'zentriq@pve!ci'; secret = 'ci-token'; skipCertificateCheck = $true } -Session $s
        # An unreachable vCenter: the sync fails and says why; the secret never comes back.
        $name = 'CI vCenter ' + (Get-Random -Maximum 99999)
        $acc = Api POST '/cloud/accounts' @{ name = $name; provider = 'vmware'; host = '127.0.0.1'; port = 9; username = 'ci@vsphere.local'; secret = 'CiVc-Value-73'; skipCertificateCheck = $true; syncHours = 24 } -Session $s
        Assert ($acc.id -and $acc.jobId) "create: $($acc | ConvertTo-Json -Compress)"
        $dupe = Raw POST '/cloud/accounts' @{ name = $name; provider = 'vmware'; host = '127.0.0.1'; username = 'x'; secret = 'x' }
        Assert ($dupe.StatusCode -eq 400) "duplicate name accepted ($($dupe.StatusCode))"
        $deadline = (Get-Date).AddSeconds(180); $a = $null
        do { Start-Sleep -Seconds 3; $a = @(Api GET '/cloud/accounts' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $acc.id } while ($a.lastStatus -notin 'failed', 'succeeded', 'partial' -and (Get-Date) -lt $deadline)
        Assert ($a.lastStatus -eq 'failed' -and $a.lastError -match '127\.0\.0\.1') "unreachable vCenter: $($a.lastStatus) / $($a.lastError)"
        $raw = (Raw GET '/cloud/accounts').Content
        Assert ($raw -notmatch 'CiVc-Value-73' -and $raw -notmatch 'zq-gcm1') 'secret returned by the API'
        Assert ($a.hasSecret) 'secret not stored'
        Api PUT "/cloud/accounts/$($acc.id)" @{ name = $name; provider = 'vmware'; host = '127.0.0.1'; port = 9; username = 'ci2@vsphere.local'; enabled = $false; syncHours = 12 } -Session $s | Out-Null
        $a = @(Api GET '/cloud/accounts' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $acc.id
        Assert ($a.hasSecret -and -not $a.enabled -and $a.settings.username -eq 'ci2@vsphere.local' -and $a.syncHours -eq 12) "edit kept secret: $($a | ConvertTo-Json -Compress -Depth 3)"
        $chg = Raw PUT "/cloud/accounts/$($acc.id)" @{ name = $name; provider = 'aws'; accessKeyId = 'AKIAABCDEFGHIJKLMNOP' }
        Assert ($chg.StatusCode -eq 400) "platform change accepted ($($chg.StatusCode))"
        # Lists, findings, changes and the reports
        $res = Api GET '/cloud/resources?kind=vm&pageSize=10' -Session $s
        Assert ($null -ne $res.total) 'resources list'
        Api GET '/cloud/findings?status=all' -Session $s | Out-Null
        Api GET '/cloud/changes?days=7' -Session $s | Out-Null
        $r1 = Api POST '/reports/run' @{ dataset = 'cloud_resources'; definition = @{ columns = @('provider', 'account', 'kind', 'name', 'state', 'linked', 'device'); filters = @() } } -Session $s
        $r2 = Api POST '/reports/run' @{ dataset = 'cloud_findings'; definition = @{ columns = @('severity', 'title', 'resource', 'status', 'severity_rank'); filters = @(@{ column = 'status'; op = 'eq'; value = 'Open' }) } } -Session $s
        $reports = Raw GET '/reports'
        Assert ($reports.Content -match 'Virtual machines without inventory' -and $reports.Content -match 'Open cloud and virtualization findings') 'built-in cloud reports missing'
        $deadline = (Get-Date).AddSeconds(120); $pa = $null
        do { Start-Sleep -Seconds 3; $pa = @(Api GET '/cloud/accounts' -Session $s | ForEach-Object { $_ }) | Where-Object id -eq $px.id } while ($pa.lastStatus -notin 'failed', 'succeeded', 'partial' -and (Get-Date) -lt $deadline)
        Assert ($pa.lastStatus -eq 'failed' -and $pa.lastError -match '127\.0\.0\.1') "unreachable Proxmox: $($pa.lastStatus) / $($pa.lastError)"
        Api DELETE "/cloud/accounts/$($px.id)" -Session $s | Out-Null
        Api DELETE "/cloud/accounts/$($acc.id)" -Session $s | Out-Null
        $gone = Raw GET "/cloud/resources/$([guid]::NewGuid())"
        Assert ($gone.StatusCode -eq 404) "unknown resource: $($gone.StatusCode)"
        "validation (Azure tenant, AWS key, Hyper-V account, Google key, Proxmox token); 9 platforms; unreachable Proxmox failed cleanly; vCenter 127.0.0.1:9 sync failed: $($a.lastError); secret write-only, kept on edit; platform change refused; reports $(@($r1.rows).Count)/$(@($r2.rows).Count) rows; built-in reports present; deleted"
    } | Out-Null

    Check 'T74' 'Asset lifecycle: own tag format and manual tags, people directory, custom field, Excel templates and bulk upload (check, all or nothing, upload)' {
        $s = Login
        function Raw([string] $Method, [string] $Path, $Body, [string] $ContentType = 'application/json') {
            $p = @{ Uri = "$base/api/v1$Path"; Method = $Method; WebSession = $s; Headers = @{ 'X-Zentriq-Request' = '1' }; ContentType = $ContentType; SkipHttpErrorCheck = $true }
            # Assign the byte array directly: an if-expression would unroll it into object[] and send numbers.
            if ($Body -is [string]) { [byte[]] $bytes = [Text.Encoding]::UTF8.GetBytes($Body); $p.Body = $bytes }
            elseif ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6) }
            $resp = Invoke-WebRequest @p
            $text = if ($resp.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($resp.Content) } else { [string]$resp.Content }
            [pscustomobject]@{ StatusCode = [int]$resp.StatusCode; Content = $text }
        }
        $bad = Raw PUT '/lifecycle/settings' @{ tagFormat = '{PREFIX}-ONLY' }
        Assert ($bad.StatusCode -eq 400 -and $bad.Content -match 'SEQ') "format without a number accepted: $($bad.StatusCode)"
        Api PUT '/lifecycle/settings' @{ tagMode = 'auto'; tagFormat = '{ORG}-{PREFIX}-{SEQ:5}'; tagOrg = 'CI'; tagStart = 100 } -Session $s | Out-Null
        $meta = Api GET '/lifecycle/meta' -Session $s
        Assert ($meta.tags.example -match '^CI-LAP-\d{5}$') "tag example: $($meta.tags.example)"
        $field = Api POST '/lifecycle/fields' @{ label = 'CI cost centre'; type = 'list'; options = @('CC-1', 'CC-2') } -Session $s
        $p = Api POST '/lifecycle/people' @{ name = 'CI Directory Person'; email = 'ci.dir@example.test'; employeeId = 'CI-E1' } -Session $s
        $dupe = Raw POST '/lifecycle/people' @{ name = 'Someone Else'; employeeId = 'CI-E1' }
        Assert ($dupe.StatusCode -eq 400) "duplicate employee ID accepted ($($dupe.StatusCode))"
        foreach ($k in 'assets', 'people', 'contracts') {
            $t = Invoke-WebRequest "$base/api/v1/lifecycle/import/$k/template" -WebSession $s
            Assert ($t.StatusCode -eq 200 -and $t.RawContentLength -gt 3000) "$k template: $($t.StatusCode) $($t.RawContentLength) bytes"
        }
        $sn1 = 'CI74-' + (Get-Random -Maximum 999999); $sn2 = 'CI74-' + (Get-Random -Maximum 999999)
        $csv = "Asset tag,Serial number,Device type,Stage,Assigned to,Location,Purchase cost,CI cost centre`r`n" +
               ",$sn1,Laptop,In use,CI Upload Person,CI Upload Site,85000,CC-1`r`nCI-OWN-$sn2,$sn2,Desktop,In stock,,,,`r`n"
        $check = (Raw POST '/lifecycle/import/assets?dryRun=true' $csv 'text/csv').Content | ConvertFrom-Json
        Assert ($check.ok -and $check.created -eq 2 -and @($check.newLocations) -contains 'CI Upload Site') "check: $($check | ConvertTo-Json -Compress -Depth 3)"
        $none = Api GET "/lifecycle/assets?q=$sn1" -Session $s
        Assert ($none.total -eq 0) 'the check saved something'
        $badCsv = $csv + ",,Toaster,,,,,`r`n"
        $failed = (Raw POST '/lifecycle/import/assets?dryRun=false' $badCsv 'text/csv').Content | ConvertFrom-Json
        Assert (-not $failed.ok -and @($failed.errors).Count -ge 1) "bad file accepted: $($failed | ConvertTo-Json -Compress -Depth 3)"
        Assert ((Api GET "/lifecycle/assets?q=$sn1" -Session $s).total -eq 0) 'a failed upload saved rows'
        $up = (Raw POST '/lifecycle/import/assets?dryRun=false' $csv 'text/csv').Content | ConvertFrom-Json
        Assert ($up.ok -and $up.created -eq 2) "upload: $($up | ConvertTo-Json -Compress -Depth 3)"
        $a1 = @((Api GET "/lifecycle/assets?q=$sn1" -Session $s).items)[0]
        $a2 = @((Api GET "/lifecycle/assets?q=$sn2" -Session $s).items)[0]
        Assert ($a1.assetTag -match '^CI-LAP-\d{5}$' -and $a1.stage -eq 'in_use' -and $a1.assignedTo -eq 'CI Upload Person') "row 1: $($a1 | ConvertTo-Json -Compress)"
        Assert ($a2.assetTag -eq "CI-OWN-$sn2" -and $a2.stage -eq 'in_stock') "row 2 (own tag): $($a2.assetTag) / $($a2.stage)"
        $d1 = Api GET "/lifecycle/assets/$($a1.id)" -Session $s
        Assert ($d1.custom.ci_cost_centre -eq 'CC-1') "custom field: $($d1.custom | ConvertTo-Json -Compress)"
        $people = @(Api GET '/lifecycle/people?q=CI' -Session $s | ForEach-Object { $_ })
        Assert (@($people | Where-Object person -eq 'CI Upload Person').Count -eq 1 -and @($people | Where-Object person -eq 'CI Directory Person').Count -eq 1) "people: $(($people | ForEach-Object person) -join ', ')"
        # Manual mode: stock without a tag stays untagged; a typed tag is used.
        Api PUT '/lifecycle/settings' @{ tagMode = 'manual' } -Session $s | Out-Null
        $sn3 = 'CI74-' + (Get-Random -Maximum 999999)
        $st = Api POST '/lifecycle/stock' @{ items = @(@{ serial = $sn3; deviceType = 'laptop' }, @{ serial = "$sn3-B"; deviceType = 'laptop'; assetTag = "MINE-$sn3" }) } -Session $s
        Assert ($null -eq @($st.created)[0].tag -and @($st.created)[1].tag -eq "MINE-$sn3") "manual mode stock: $($st.created | ConvertTo-Json -Compress)"
        Api PUT '/lifecycle/settings' @{ tagMode = 'auto'; tagFormat = '{PREFIX}-{SEQ:6}'; tagOrg = ''; tagStart = 1 } -Session $s | Out-Null
        Api DELETE "/lifecycle/fields/$($field.id)" -Session $s | Out-Null
        Api DELETE "/lifecycle/people/$($p.id)" -Session $s | Out-Null
        "format CI-{PREFIX}-{SEQ:5} → $($a1.assetTag); own tag kept; templates x3; check saved nothing; bad file all-or-nothing ($(@($failed.errors).Count) problem(s)); 2 uploaded with person, new location, custom field; manual mode: no automatic tag, typed tag kept; settings restored"
    } | Out-Null

    Check 'T75' 'Backup & Restore: password, schedule, folder and SMB share destinations, back up now, retention, history, download, restore from a destination with the password' {
        $s = Login
        function Raw75([string] $Method, [string] $Path, $Body) {
            $p = @{ Uri = "$base/api/v1$Path"; Method = $Method; WebSession = $s; Headers = @{ 'X-Zentriq-Request' = '1' }; ContentType = 'application/json'; SkipHttpErrorCheck = $true }
            if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6) }
            $r = Invoke-WebRequest @p
            [pscustomobject]@{ StatusCode = [int]$r.StatusCode; Content = (Body $r) }
        }
        $bkPass = 'Zq-Backup-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
        $shareUser = 'zqbk' + (Get-Random -Maximum 99999)
        $sharePass = 'Sh-' + [Guid]::NewGuid().ToString('N').Substring(0, 14) + '-9a'
        $folder = 'C:\ZqBackupDest'
        $shareDir = 'C:\ZqShare'
        try {
            # Backup password and schedule (validation first).
            $short = Raw75 PUT '/backup/password' @{ password = 'short' }
            Assert ($short.StatusCode -eq 400) "short password accepted: $($short.StatusCode)"
            Api PUT '/backup/password' @{ password = $bkPass; confirm = $bkPass } -Session $s | Out-Null
            $badTime = Raw75 PUT '/backup/settings' @{ schedule = @{ enabled = $true; frequency = 'daily'; time = '25:00'; weekdays = @(0); day = 1 } }
            Assert ($badTime.StatusCode -eq 400) "time 25:00 accepted: $($badTime.StatusCode)"
            $sch = Api PUT '/backup/settings' @{ schedule = @{ enabled = $true; frequency = 'weekly'; time = '02:15'; weekdays = @(3, 1); day = 1 }; keepLocal = 20 } -Session $s
            Assert ($sch.scheduleText -eq 'Every Monday, Wednesday at 02:15') "schedule: $($sch.scheduleText)"

            # Destinations: a folder on this server and an SMB share (local account) on this machine.
            New-LocalUser -Name $shareUser -Password (ConvertTo-SecureString $sharePass -AsPlainText -Force) -PasswordNeverExpires -AccountNeverExpires | Out-Null
            New-Item -ItemType Directory -Force -Path $shareDir | Out-Null
            & icacls $shareDir /grant "${shareUser}:(OI)(CI)M" | Out-Null
            New-SmbShare -Name 'ZqBackups' -Path $shareDir -ChangeAccess "$env:COMPUTERNAME\$shareUser" | Out-Null
            $badSmb = Raw75 POST '/backup/destinations' @{ name = 'bad'; kind = 'smb'; settings = @{ path = 'localhost/ZqBackups' } }
            Assert ($badSmb.StatusCode -eq 400 -and $badSmb.Content -match 'server') "bad UNC accepted: $($badSmb.StatusCode)"
            $fd = Api POST '/backup/destinations' @{ name = 'CI folder'; kind = 'folder'; keep = 2; settings = @{ path = $folder } } -Session $s
            $sd = Api POST '/backup/destinations' @{ name = 'CI share'; kind = 'smb'; keep = 5; settings = @{ path = '\\localhost\ZqBackups\zentriq'; user = "$env:COMPUTERNAME\$shareUser" }; password = $sharePass } -Session $s
            $ft = Api POST "/backup/destinations/$($fd.id)/test" -Session $s
            Assert $ft.ok "folder test: $($ft.message)"
            $st = Api POST "/backup/destinations/$($sd.id)/test" -Session $s
            $offline = Api POST '/backup/destinations/test' @{ name = 'nowhere'; kind = 's3'; keep = 3; settings = @{ bucket = 'zq-ci'; endpoint = 'https://127.0.0.1:9'; pathStyle = $true; user = 'AKID' }; password = 'x' } -Session $s
            Assert (-not $offline.ok) 'unreachable S3 endpoint reported OK'
            $ovRaw = Raw75 GET '/backup'
            Assert ($ovRaw.Content -notmatch [regex]::Escape($sharePass) -and $ovRaw.Content -notmatch [regex]::Escape($bkPass)) 'overview exposes a password'
            $ov = $ovRaw.Content | ConvertFrom-Json
            Assert ($ov.password.set -and @($ov.destinations).Count -eq 2) "overview: password $($ov.password.set), $(@($ov.destinations).Count) destination(s)"

            # A marker that exists in the first backup only.
            $marker = 'CI Backup Marker ' + (Get-Random -Maximum 999999)
            $mp = Api POST '/lifecycle/people' @{ name = $marker } -Session $s
            $job = Api POST '/backup/run' -Session $s
            & $ctl maintenance run-pending | Out-Host
            $runs = Api GET '/backup/runs' -Session $s
            $first = @($runs.items)[0]
            Assert ($first.status -eq 'succeeded' -and $first.trigger -eq 'manual' -and $first.passwordProtected) "run: $($first | ConvertTo-Json -Compress -Depth 4)"
            Assert (@($first.destinations | Where-Object status -eq 'ok').Count -eq 2) "copies: $($first.destinations | ConvertTo-Json -Compress)"
            Assert ($first.contents.certificates.Count -ge 2 -and $first.contents.dataProtectionKeys -ge 1) "contents: $($first.contents | ConvertTo-Json -Compress)"
            Assert (Test-Path (Join-Path $folder $first.file)) 'not copied to the folder'
            Assert (Test-Path (Join-Path "$shareDir\zentriq" $first.file)) 'not copied to the share'
            $hdr = Get-Content (Join-Path $folder $first.file) -TotalCount 2 | Select-Object -Last 1 | ConvertFrom-Json
            Assert ($hdr.Format -eq 2 -and @($hdr.Keys | Where-Object Type -eq 'password').Count -eq 1) "header: $($hdr | ConvertTo-Json -Compress -Depth 4)"
            $dl = Invoke-WebRequest "$base/api/v1/backup/files/$($first.file)" -WebSession $s -OutFile (Join-Path $env:TEMP 'zq-dl.zbak') -PassThru
            Assert ($dl.StatusCode -eq 200 -and (Get-Item (Join-Path $env:TEMP 'zq-dl.zbak')).Length -eq $first.sizeBytes) 'download differs'
            $pwFile = Join-Path $env:TEMP 'zq-bk-pass.txt'
            Set-Content $pwFile $bkPass -NoNewline
            & $ctl backup-verify (Join-Path $folder $first.file) --password-file $pwFile | Out-Host
            Assert ($LASTEXITCODE -eq 0) "backup-verify exit $LASTEXITCODE"

            # Retention: the folder keeps 2.
            & $ctl backup | Out-Host; Assert ($LASTEXITCODE -eq 0) "backup 2 exit $LASTEXITCODE"
            & $ctl backup | Out-Host; Assert ($LASTEXITCODE -eq 0) "backup 3 exit $LASTEXITCODE"
            $inFolder = @(Get-ChildItem $folder -Filter 'zentriq-*.zbak')
            Assert ($inFolder.Count -eq 2) "folder holds $($inFolder.Count) backups (keep 2)"
            Assert (@(Get-ChildItem "$shareDir\zentriq" -Filter 'zentriq-*.zbak').Count -eq 3) 'share does not hold 3 backups'

            # Restore the first backup from the share with the password: the marker comes back.
            Api DELETE "/lifecycle/people/$($mp.id)" -Session $s | Out-Null
            $files = Api GET "/backup/destinations/$($sd.id)/files" -Session $s
            Assert (@($files | Where-Object name -eq $first.file).Count -eq 1) "share listing: $(($files | ForEach-Object name) -join ', ')"
            $noConfirm = Raw75 POST '/backup/restore' @{ source = 'destination'; destinationId = $sd.id; file = $first.file; password = $bkPass; confirm = 'yes' }
            Assert ($noConfirm.StatusCode -eq 400) "restore without RESTORE accepted: $($noConfirm.StatusCode)"
            Api POST '/backup/restore' @{ source = 'destination'; destinationId = $sd.id; file = $first.file; password = $bkPass; confirm = 'RESTORE' } -Session $s | Out-Null
            & $ctl maintenance run-pending | Out-Host
            Wait-Ready 300 | Out-Null
            $s = Login
            $back = @(Api GET "/lifecycle/people?q=$([uri]::EscapeDataString($marker))" -Session $s | ForEach-Object { $_ } | Where-Object person -eq $marker)
            Assert ($back.Count -eq 1) 'marker not restored'
            $after = Api GET '/backup' -Session $s
            Assert ($after.lastRestore.file -eq $first.file -or $after.lastRestore.file -like "*$($first.file)") "last restore: $($after.lastRestore | ConvertTo-Json -Compress)"
            $restoredRun = @((Api GET '/backup/runs' -Session $s).items | Where-Object file -eq $first.file)[0]
            Assert ($restoredRun.status -eq 'succeeded') "the backup's own run after restore: $($restoredRun.status)"
            Api DELETE "/lifecycle/people/$((@($back)[0]).id)" -Session $s | Out-Null
            "password + weekly schedule; folder test OK; share test $(if ($st.ok) { 'OK' } else { 'FAILED: ' + $st.message }); unreachable S3 refused; backup copied to 2 destinations (format 2, password, $($first.contents.dataProtectionKeys) MFA key file(s)); folder keeps 2 of 3; download; CLI verify with the password; restored from the share: marker back"
        } finally {
            $s = Login
            foreach ($d in @(Api GET '/backup' -Session $s).destinations) { Api DELETE "/backup/destinations/$($d.id)" -Session $s | Out-Null }
            Api DELETE '/backup/password' -Session $s | Out-Null
            Api PUT '/backup/settings' @{ schedule = @{ enabled = $true; frequency = 'daily'; time = '01:30'; weekdays = @(0); day = 1 }; keepLocal = 14 } -Session $s | Out-Null
            Remove-SmbShare -Name 'ZqBackups' -Force -ErrorAction SilentlyContinue
            Remove-LocalUser -Name $shareUser -ErrorAction SilentlyContinue
            Remove-Item $folder, $shareDir, (Join-Path $env:TEMP 'zq-dl.zbak'), (Join-Path $env:TEMP 'zq-bk-pass.txt') -Recurse -Force -ErrorAction SilentlyContinue
        }
    } | Out-Null

    Check 'T76' 'Roles and groups: custom Non-IT role through a group sees only non-IT assets, its dashboard and reports; built-in roles read-only; rights change without signing out' {
        $s = Login
        function Raw76([string] $Method, [string] $Path, $Body, $Session) {
            $p = @{ Uri = "$base/api/v1$Path"; Method = $Method; WebSession = $Session; Headers = @{ 'X-Zentriq-Request' = '1' }; ContentType = 'application/json'; SkipHttpErrorCheck = $true }
            if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6) }
            $r = Invoke-WebRequest @p
            [pscustomobject]@{ StatusCode = [int]$r.StatusCode; Content = (Body $r) }
        }
        $roles = Api GET '/roles' -Session $s
        foreach ($id in 'admin', 'nonit', 'nonit_viewer', 'it_assets', 'lifecycle', 'cloud', 'security') { Assert (@($roles | Where-Object id -eq $id).Count -eq 1) "built-in role $id missing" }
        $builtIn = Raw76 PUT '/roles/nonit' @{ name = 'Changed'; permissions = @('nonit.view') } $s
        Assert ($builtIn.StatusCode -ge 400 -and $builtIn.StatusCode -lt 500) "built-in role changed: $($builtIn.StatusCode)"
        $unknown = Raw76 POST '/roles' @{ name = 'CI bad'; permissions = @('nonit.view', 'everything.please') } $s
        Assert ($unknown.StatusCode -eq 400) "unknown permission accepted: $($unknown.StatusCode)"
        $role = Api POST '/roles' @{ name = 'CI Facilities'; description = 'Non-IT only'; permissions = @('dashboard.view', 'nonit.view', 'nonit.manage', 'reports.view') } -Session $s
        $group = Api POST '/groups' @{ name = 'CI Facilities team'; roles = @($role.id); members = @() } -Session $s
        $uMail = "facilities$(Get-Random -Maximum 99999)@ci.zentriq.test"
        $uPass = 'Fa-' + [Guid]::NewGuid().ToString('N').Substring(0, 16) + '-x9'
        $user = Api POST '/users' @{ email = $uMail; displayName = 'CI Facilities'; password = $uPass; roles = @(); groups = @($group.id) } -Session $s
        try {
            $f = New-Object Microsoft.PowerShell.Commands.WebRequestSession
            for ($i = 0; ; $i++) {
                $lr = Invoke-WebRequest "$base/api/v1/auth/login" -Method Post -Body (@{ email = $uMail; password = $uPass } | ConvertTo-Json) -ContentType 'application/json' -Headers @{ 'X-Zentriq-Request' = '1' } -WebSession $f -SkipHttpErrorCheck
                if ($lr.StatusCode -eq 429 -and $i -lt 5) { Start-Sleep 15; continue }
                Assert ($lr.StatusCode -eq 200) "facilities login $($lr.StatusCode)"; break
            }
            $me = Api GET '/auth/me' -Session $f
            Assert (@($me.permissions) -contains 'nonit.manage' -and @($me.permissions) -notcontains 'assets.view') "permissions: $(@($me.permissions) -join ',')"
            $allowed = @{ '/nonit/assets' = 200; '/nonit/summary' = 200; '/dashboards' = 200; '/reports/datasets' = 200 }
            $denied = @('/assets', '/software', '/accessories/items', '/lifecycle/summary', '/cloud/summary', '/activity', '/audit', '/users', '/roles', '/backup', '/vulnerabilities', '/logs')
            foreach ($k in $allowed.Keys) { $r = Raw76 GET $k $null $f; Assert ($r.StatusCode -eq 200) "$k returned $($r.StatusCode)" }
            $leaks = @(foreach ($k in $denied) { $r = Raw76 GET $k $null $f; if ($r.StatusCode -notin 403, 404) { "$k=$($r.StatusCode)" } })
            Assert ($leaks.Count -eq 0) "reachable without permission: $($leaks -join ', ')"
            $ds = (Api GET '/reports/datasets' -Session $f).datasets
            Assert ((@($ds | ForEach-Object key) -join ',') -eq 'nonit_assets') "report sources: $(@($ds | ForEach-Object key) -join ',')"
            $escalate = Raw76 POST '/roles' @{ name = 'CI escalate'; permissions = @('users.manage') } $f
            Assert ($escalate.StatusCode -eq 403) "role editing allowed: $($escalate.StatusCode)"
            $inUse = Raw76 DELETE "/roles/$($role.id)" $null $s
            Assert ($inUse.StatusCode -eq 409) "role in use deleted: $($inUse.StatusCode)"
            # Viewer instead of manager: the same session loses write access at its next request.
            Api PUT "/groups/$($group.id)" @{ name = 'CI Facilities team'; roles = @('nonit_viewer'); members = @($user.id) } -Session $s | Out-Null
            $me2 = Api GET '/auth/me' -Session $f
            Assert (@($me2.permissions) -contains 'nonit.view' -and @($me2.permissions) -notcontains 'nonit.manage') "after change: $(@($me2.permissions) -join ',')"
            $write = Raw76 POST '/nonit/assets' @{ name = 'CI chair' } $f
            Assert ($write.StatusCode -eq 403) "viewer could add a non-IT asset: $($write.StatusCode)"
            "7 built-in roles (read-only); custom role via group: non-IT, dashboard and reports only ($($denied.Count) other areas refused, report sources: nonit_assets); no escalation; role in use kept; viewer applied to the live session"
        } finally {
            Raw76 PUT "/users/$($user.id)" @{ status = 'disabled'; roles = @(); groups = @() } $s | Out-Null
            Raw76 DELETE "/groups/$($group.id)" $null $s | Out-Null
            Raw76 DELETE "/roles/$($role.id)" $null $s | Out-Null
        }
    } | Out-Null

    Check 'T77' 'Log retention and export: one year by default, validation, schedule every 6 months, export a period now, archive download with manifest, CSV download of the audit and activity logs' {
        $s = Login
        $info = Api GET '/logs' -Session $s
        Assert ($info.settings.auditRetentionDays -eq 365 -and $info.settings.activityRetentionDays -eq 365 -and $info.settings.archiveBeforeDelete) "defaults: $($info.settings | ConvertTo-Json -Compress -Depth 4)"
        $p = @{ Uri = "$base/api/v1/logs/settings"; Method = 'PUT'; WebSession = $s; Headers = @{ 'X-Zentriq-Request' = '1' }; ContentType = 'application/json'; SkipHttpErrorCheck = $true; Body = (@{ auditRetentionDays = 30 } | ConvertTo-Json) }
        Assert ((Invoke-WebRequest @p).StatusCode -eq 400) '30-day audit retention accepted'
        $saved = Api PUT '/logs/settings' @{ auditRetentionDays = 730; activityRetentionDays = 365; schedule = @{ enabled = $true; everyMonths = 6; includeAudit = $true; includeActivity = $true } } -Session $s
        Assert ($saved.auditRetentionDays -eq 730 -and $saved.schedule.everyMonths -eq 6) "saved: $($saved | ConvertTo-Json -Compress -Depth 4)"
        $to = (Get-Date).ToUniversalTime().AddMinutes(1); $from = $to.AddDays(-30)
        $req = Api POST '/logs/archives' @{ from = $from.ToString('o'); to = $to.ToString('o'); includeAudit = $true; includeActivity = $true } -Session $s
        & $ctl maintenance run-pending | Out-Host
        $arch = (Api GET '/logs/archives' -Session $s).items
        $mine = @($arch | Where-Object id -eq $req.id)[0]
        Assert ($mine.status -eq 'succeeded' -and $mine.auditRows -gt 0 -and $mine.available) "archive: $($mine | ConvertTo-Json -Compress -Depth 4)"
        $sched = @($arch | Where-Object trigger -eq 'schedule')
        Assert ($sched.Count -ge 1 -and $sched[0].status -eq 'succeeded') "scheduled archive: $($sched | ConvertTo-Json -Compress -Depth 4)"
        $zipPath = Join-Path $env:TEMP 'zq-logs.zip'
        Invoke-WebRequest "$base/api/v1/logs/archives/$($mine.id)/file" -WebSession $s -OutFile $zipPath
        Assert ((Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower() -eq $mine.sha256) 'archive checksum differs'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $names = @($zip.Entries | ForEach-Object FullName | Sort-Object)
            Assert (($names -join ',') -eq 'activity.csv,audit.csv,manifest.json') "zip: $($names -join ',')"
            $sr = New-Object IO.StreamReader($zip.GetEntry('manifest.json').Open()); $manifest = $sr.ReadToEnd() | ConvertFrom-Json; $sr.Dispose()
            Assert ($manifest.audit.rows -eq $mine.auditRows -and $manifest.audit.sha256.Length -eq 64) "manifest: $($manifest | ConvertTo-Json -Compress -Depth 4)"
        } finally { $zip.Dispose(); Remove-Item $zipPath -Force -ErrorAction SilentlyContinue }
        $q = "from=$([uri]::EscapeDataString($from.ToString('o')))&to=$([uri]::EscapeDataString($to.ToString('o')))"
        $csv = Invoke-WebRequest "$base/api/v1/audit/export?$q" -WebSession $s
        $csvText = Body $csv
        Assert ($csv.Headers['Content-Disposition'] -match 'zentriq-audit-' -and $csvText -match 'occurred_at_utc' -and $csvText -match 'logs.export_request') 'audit CSV incomplete'
        $act = Invoke-WebRequest "$base/api/v1/activity/export?$q" -WebSession $s
        Assert ((Body $act) -match 'requested_at_utc') 'activity CSV has no header'
        Api PUT '/logs/settings' @{ auditRetentionDays = 365; activityRetentionDays = 365; schedule = @{ enabled = $false; everyMonths = 3; includeAudit = $true; includeActivity = $true } } -Session $s | Out-Null
        "defaults 1 year; 30 days refused; export of 30 days: $($mine.auditRows) audit / $($mine.activityRows) activity entries, checksum and manifest OK; scheduled half-year archive made; CSV downloads OK"
    } | Out-Null

    Check 'T71' 'Installation type: Advanced recorded, change to Standard (Remote support off, section locked) and back with Setup Update' {
        $s = Login
        $st = Api GET '/licensing/status' -Session $s
        Assert ($st.edition -eq 'trial') "edition: $($st.edition)"
        $secs = @($st.sections)
        Assert ($secs.Count -eq 6 -and @($secs | Where-Object { -not $_.installed -or -not $_.enabled }).Count -eq 0) "trial on Advanced installation: $($secs | ConvertTo-Json -Compress)"
        Assert ((Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Console').Sections -eq 'all') "registry Sections: $((Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Console').Sections)"
        $code = (Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' | Get-ItemProperty |
                 Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Zentriq' -and $_.PSObject.Properties['WindowsInstaller'] -and $_.WindowsInstaller -eq 1 } | Select-Object -First 1).PSChildName
        Assert $code 'Zentriq MSI product code not found'
        function Update-Sections([string] $Sections, [string] $Name) {
            $log = Join-Path $OutDir "$Name.log"
            $p = Start-Process msiexec.exe -ArgumentList '/i', $code, 'REINSTALL=ALL', 'REINSTALLMODE=mus', "ZQ_SECTIONS=$Sections", '/qn', '/norestart', '/l*v', "`"$log`"" -Wait -PassThru
            if ($p.ExitCode -notin 0, 3010) { throw "Update to $Sections exited with $($p.ExitCode); see $log" }
            Wait-Ready | Out-Null
        }
        Update-Sections 'standard' 'update-standard'
        $s = Login
        $st = Api GET '/licensing/status' -Session $s
        $remote = @($st.sections) | Where-Object id -eq 'remote'
        $secure = @($st.sections) | Where-Object id -eq 'secure'
        Assert (-not $remote.installed -and -not $secure.installed) "after Standard: $($st.sections | ConvertTo-Json -Compress)"
        $blocked = Invoke-WebRequest "$base/api/v1/vulnerabilities/overview" -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -SkipHttpErrorCheck
        Assert ([int]$blocked.StatusCode -eq 403) "vulnerabilities API on a Standard installation: $([int]$blocked.StatusCode)"
        $svc = Get-CimInstance Win32_Service -Filter "Name='ZentriqRemote'"
        Assert ($svc.StartMode -eq 'Disabled' -and $svc.State -ne 'Running') "ZentriqRemote on Standard: $($svc.StartMode) / $($svc.State)"
        Assert ((Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Console').Sections -eq 'overview,discover,infrastructure,insight') 'registry not Standard'
        $inv = Invoke-WebRequest "$base/api/v1/assets" -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -SkipHttpErrorCheck
        Assert ([int]$inv.StatusCode -eq 200) "inventory on Standard: $([int]$inv.StatusCode)"

        Update-Sections 'all' 'update-advanced'
        $s = Login
        $st = Api GET '/licensing/status' -Session $s
        Assert (@($st.sections | Where-Object { -not $_.enabled }).Count -eq 0) "after Advanced: $($st.sections | ConvertTo-Json -Compress)"
        $deadline = (Get-Date).AddMinutes(3)
        do { Start-Sleep 5; $svc = Get-CimInstance Win32_Service -Filter "Name='ZentriqRemote'" } while ($svc.State -ne 'Running' -and (Get-Date) -lt $deadline)
        Assert ($svc.StartMode -ne 'Disabled' -and $svc.State -eq 'Running') "ZentriqRemote after Advanced: $($svc.StartMode) / $($svc.State)"
        "trial: 6 sections; Standard: Remote support and Secure & remediate removed, vulnerabilities API 403, remote service disabled, inventory works; Advanced again: all sections, remote service running"
    } | Out-Null

    if ($UpgradeBundle) {
        Check 'T13' 'Upgrade to the next release preserves data and trial' {
            Run-Setup $UpgradeBundle @() 'upgrade' | Out-Null
            Wait-Ready | Out-Null
            $s = Setup-Status
            Assert ($s.trialExpiresAt -eq $script:trialEnd) 'trial changed after upgrade'
            $ver = (Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Console').Version
            Login | Out-Null
            "now $ver (database reports $($s.productVersion)); trial unchanged"
        } | Out-Null

        # The upgraded server now serves the next agent package; the agent of this build updates itself from the console.
        if (Test-Path $agentMsi) {
            Check 'T47' 'Agent update from the console: agent downloads the package over mTLS, verifies it and updates in place' {
                $s = Login
                $tok = Api POST '/agents/enrollment-tokens' @{ name = 'ci-update'; validDays = 1; maxUses = 1; serverUrl = 'https://localhost:8444' } -Session $s
                $log = Join-Path $OutDir 'agent-install-for-update.log'
                $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$agentMsi`"", '/qn', "SERVERURL=https://localhost:8444", "ENROLLMENTCODE=$($tok.code)", 'CHECKININTERVAL=2', 'INVENTORYINTERVAL=30', '/l*v', "`"$log`"" -Wait -PassThru
                Assert ($p.ExitCode -eq 0) "agent MSI exit $($p.ExitCode)"
                $deadline = (Get-Date).AddMinutes(5)
                do {
                    Start-Sleep 5
                    $a = @(Api GET '/agents' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.status -eq 'active' -and $_.assetId -and $_.canSelfUpdate } | Select-Object -First 1
                } while (-not $a -and (Get-Date) -lt $deadline)
                Assert $a 'agent did not enroll, send inventory and report the self-update capability'
                Assert ($a.updateAvailable) "no update offered: agent $($a.version), server package $($a.latestVersion)"
                $before = @{ id = $a.id; version = $a.version; thumb = (Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent').CertThumbprint }
                $r = Api POST '/agents/update' @{ agentIds = @($a.id) } -Session $s
                Assert ($r.queued -eq 1) "update not queued: $($r.message)"
                $deadline = (Get-Date).AddMinutes(10)
                do {
                    Start-Sleep 15
                    $a = @(Api GET '/agents' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.id -eq $before.id } | Select-Object -First 1
                } while (-not ($a.update -and $a.update.status -in 'succeeded', 'failed') -and (Get-Date) -lt $deadline)
                if (-not ($a.update -and $a.update.status -eq 'succeeded')) {
                    Get-ChildItem 'C:\ProgramData\Zentriq\Agent\Logs' -ErrorAction SilentlyContinue | Copy-Item -Destination $OutDir -ErrorAction SilentlyContinue
                    $st = if ($a -and $a.update) { "$($a.update.status) - $($a.update.detail)" } else { 'no update record' }
                    throw "update status: $st; agent reports $(if ($a) { $a.version } else { '?' })"
                }
                Assert ($a.version -eq $a.latestVersion) "agent reports $($a.version), expected $($a.latestVersion)"
                Assert (-not $a.updateAvailable) 'still offered an update'
                Assert ((Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent').CertThumbprint -eq $before.thumb) 'enrollment/certificate not kept'
                Assert ((Get-Service ZentriqAgent).Status -eq 'Running') 'agent service not running after the update'
                $arp = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Zentriq Agent' })
                Assert ($arp.Count -eq 1) "$($arp.Count) 'Zentriq Agent' entries in Apps and Features"
                $act = Api GET '/activity?kind=agent-update' -Session $s
                Assert (@($act.items | Where-Object { $_.status -eq 'succeeded' -and $_.target -eq $a.version }).Count -eq 1) 'update not in the Activity log'
                $again = Api POST '/agents/update' @{ all = $true } -Session $s
                Assert ($again.queued -eq 0) 'an up-to-date agent was queued again'
                foreach ($e in $arp) { Start-Process msiexec.exe -ArgumentList '/x', $e.PSChildName, '/qn' -Wait | Out-Null }
                "$($before.version) -> $($a.version) in place (same registration and certificate, one Apps entry); Activity log: $($a.update.detail)"
            } | Out-Null
            Check 'T79' 'Automatic agent update: off by default; when on, an older agent updates itself to the server version without a console request' {
                $s = Login
                $st = Api GET '/agents/settings' -Session $s
                Assert (-not $st.settings.autoUpdate) 'automatic updates are on by default'
                Api PUT '/agents/settings' @{ checkinMinutes = 30; inventoryMinutes = 180; uninstallProtection = $false; autoUpdate = $true; autoUpdateMax = 5 } -Session $s | Out-Null
                $tok = Api POST '/agents/enrollment-tokens' @{ name = 'ci-autoupdate'; validDays = 1; maxUses = 1; serverUrl = 'https://localhost:8444' } -Session $s
                $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$agentMsi`"", '/qn', "SERVERURL=https://localhost:8444", "ENROLLMENTCODE=$($tok.code)", 'CHECKININTERVAL=2', 'INVENTORYINTERVAL=30', '/l*v', "`"$(Join-Path $OutDir 'agent-install-autoupdate.log')`"" -Wait -PassThru
                Assert ($p.ExitCode -eq 0) "agent MSI exit $($p.ExitCode)"
                try {
                    $deadline = (Get-Date).AddMinutes(12)
                    do {
                        Start-Sleep 15
                        $a = @(Api GET '/agents' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.status -eq 'active' -and $_.assetId -and $_.enrollmentCode -eq 'ci-autoupdate' } | Select-Object -First 1
                    } while (-not ($a -and $a.version -eq $a.latestVersion -and $a.update -and $a.update.status -eq 'succeeded') -and (Get-Date) -lt $deadline)
                    if (-not ($a -and $a.update -and $a.update.status -eq 'succeeded')) {
                        Get-ChildItem 'C:\ProgramData\Zentriq\Agent\Logs' -ErrorAction SilentlyContinue | Copy-Item -Destination $OutDir -ErrorAction SilentlyContinue
                        throw "no automatic update: agent $(if ($a) { "$($a.version), latest $($a.latestVersion), update $($a.update | ConvertTo-Json -Compress)" } else { 'not enrolled' })"
                    }
                    Assert ($a.update.automatic) 'the update was not marked automatic'
                    $act = Api GET '/activity?kind=agent-update' -Session $s
                    Assert (@($act.items | Where-Object { $_.status -eq 'succeeded' -and $_.target -eq $a.version -and -not $_.requestedBy }).Count -ge 1) 'automatic update not in the Activity log'
                    "agent installed at the older version updated itself to $($a.version) without a console request ($($a.update.detail))"
                } finally {
                    Api PUT '/agents/settings' @{ checkinMinutes = 30; inventoryMinutes = 180; uninstallProtection = $false; autoUpdate = $false; autoUpdateMax = 25 } -Session $s | Out-Null
                    $arp = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Zentriq Agent' })
                    foreach ($e in $arp) { Start-Process msiexec.exe -ArgumentList '/x', $e.PSChildName, '/qn' -Wait | Out-Null }
                }
            } | Out-Null
        } else { Record 'T47' 'Agent update from the console' 'BLOCKED' 'agent package from T26 not available' }
    } else {
        Record 'T13' 'Upgrade to the next release' 'NOT RUN' 'no -UpgradeBundle supplied'
        Record 'T47' 'Agent update from the console' 'NOT RUN' 'needs -UpgradeBundle (a newer agent package on the server)'
        Record 'T79' 'Automatic agent update' 'NOT RUN' 'needs -UpgradeBundle (a newer agent package on the server)'
    }

    # ---- Zentriq Agent for Linux (Ubuntu in WSL on this runner) ----------------------------------------
    $wslDistro = $env:ZQ_WSL_DISTRO
    $wslReady = $wslDistro -and (Get-Command wsl.exe -ErrorAction SilentlyContinue) -and ((& wsl.exe -d $wslDistro -u root -- uname -s 2>$null) -match 'Linux')
    if ($wslReady) {
        Check 'T81' 'Linux agent: one-line install from the console command (pinned key), enrollment, inventory with distribution / edition / server or desktop, live connection, Linux security checks, package uninstall through the agent, tamper-protected removal' {
            $s = Login
            function Wsl([string] $Script, [int] $TimeoutSec = 600) {
                $f = Join-Path $OutDir ("wsl-" + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.sh')
                [IO.File]::WriteAllText($f, "set -e`n" + ($Script -replace "`r", ''))
                $lin = (& wsl.exe -d $wslDistro -u root -- wslpath -a ($f -replace '\\', '/')).Trim()
                $o = (& wsl.exe -d $wslDistro -u root -- bash $lin 2>&1 | Out-String)
                $code = $LASTEXITCODE
                Add-Content (Join-Path $OutDir 'linux-agent-wsl.log') "---- $Script`n$o"
                if ($code -ne 0) { throw "WSL script failed ($code): $($o.Trim() -split "`n" | Select-Object -Last 8)" }
                $o
            }
            $pkg = Api GET '/agents/package' -Session $s
            Assert ($pkg.linuxPackages -and @($pkg.linuxPackages.files).Count -eq 6) "Linux packages on the server: $($pkg.linuxPackages | ConvertTo-Json -Compress -Depth 4)"
            Api PUT '/agents/settings' @{ checkinMinutes = 30; inventoryMinutes = 180; uninstallProtection = $true; autoUpdate = $false; autoUpdateMax = 25 } -Session $s | Out-Null
            try {
                $tok = Api POST '/agents/enrollment-tokens' @{ name = 'ci-linux'; validDays = 1; maxUses = 1; serverUrl = 'https://localhost:8444'; checkinMinutes = 2; inventoryMinutes = 30 } -Session $s
                Assert ($tok.linuxInstallCommand -match "^curl -fsSLk --pinnedpubkey 'sha256//[A-Za-z0-9+/=]{44}' https://localhost:8444/api/agent/v1/linux/install\.sh \| sudo sh -s -- ") "install command: $($tok.linuxInstallCommand)"
                # A small package to remove later through the console.
                Wsl @'
mkdir -p /tmp/zqdummy/DEBIAN
printf 'Package: zq-ci-dummy\nVersion: 1.0\nArchitecture: all\nMaintainer: Zentriq CI <ci@example.com>\nDescription: Zentriq CI test package\n' > /tmp/zqdummy/DEBIAN/control
dpkg-deb --build /tmp/zqdummy /tmp/zq-ci-dummy.deb >/dev/null && dpkg -i /tmp/zq-ci-dummy.deb >/dev/null
'@ | Out-Null
                $out = Wsl ($tok.linuxInstallCommand + "`nzentriq-agent --identify`nzentriq-agent --status || true")
                Assert ($out -match 'Distribution:\s+ubuntu \(debian family\)' -and $out -match 'Installation:\s+Server') "identify: $out"
                # The service loop (no systemd in WSL 1): live connection, check-ins and actions.
                Wsl "setsid nohup /opt/zentriq-agent/zentriq-agent --run >/var/log/zentriq-agent/run.out 2>&1 < /dev/null &`nsleep 3" | Out-Null
                $deadline = (Get-Date).AddMinutes(3)
                do { Start-Sleep 5; $a = @(Api GET '/agents' -Session $s | ForEach-Object { $_ }) | Where-Object { $_.enrollmentCode -eq 'ci-linux' -and $_.status -eq 'active' } | Select-Object -First 1 }
                while (-not ($a -and $a.assetId -and $a.online) -and (Get-Date) -lt $deadline)
                Assert ($a -and $a.assetId) "Linux agent not enrolled with inventory: $($a | ConvertTo-Json -Compress -Depth 3)"
                Assert ($a.platform -eq 'linux-x64') "platform $($a.platform)"
                Assert ($a.online -and $a.live) "not online over the live connection (online $($a.online), live $($a.live))"
                $script:linuxAsset = $a.assetId
                $inv = Api GET "/assets/$($a.assetId)/inventory" -Session $s
                $os = $inv.document.os
                Assert ($os.family -eq 'linux' -and $os.distribution -eq 'ubuntu' -and $os.distributionFamily -eq 'debian') "os: $($os | ConvertTo-Json -Compress)"
                Assert ($os.isServer -eq $true -and $os.editionId -match 'Ubuntu' -and $os.kernel -and $os.packageManager -eq 'dpkg') "edition/role: $($os | ConvertTo-Json -Compress)"
                # Installed packages are kept in the software inventory (not in the stored document).
                $sw = foreach ($n in 'zentriq-agent', 'zq-ci-dummy') { @(Api GET "/inventory/software/installs?name=$n" -Session $s | ForEach-Object { $_ }) | Where-Object { $_.assetId -eq $a.assetId } }
                Assert (@($sw).Count -eq 2) "packages in the software inventory: $(@($sw).Count) of zentriq-agent, zq-ci-dummy"
                Assert ($inv.document.security.linux) 'Linux security settings missing'
                # Linux checks in Security Configuration.
                Api POST '/security/evaluate' -Session $s | Out-Null
                $deadline = (Get-Date).AddMinutes(2)
                do { Start-Sleep 10; $sec = Api GET "/security/devices/$($a.assetId)" -Session $s; $lin = @($sec.results | Where-Object { $_.rule -like 'SL-*' }) } while ($lin.Count -eq 0 -and (Get-Date) -lt $deadline)
                Assert ($lin.Count -ge 10) "Linux security results: $($lin.Count)"
                Assert (-not ($sec.results | Where-Object { $_.rule -like 'SC-*' -and $_.status -in 'pass', 'fail', 'unknown' })) 'Windows checks evaluated on a Linux device'
                # Remove a package through the console.
                $r = Api POST '/software/uninstall' @{ name = 'zq-ci-dummy'; assetIds = @($a.assetId) } -Session $s
                Assert ($r.sent -eq 1) "uninstall sent $($r.sent): $($r.message)"
                $deadline = (Get-Date).AddMinutes(3)
                do { Start-Sleep 5; $act = (Api GET "/activity?kind=uninstall&assetId=$($a.assetId)" -Session $s).items | Select-Object -First 1 } while ($act.status -notin 'succeeded', 'failed' -and (Get-Date) -lt $deadline)
                Assert ($act.status -eq 'succeeded') "package uninstall $($act.status): $($act.detail)"
                Assert ((Wsl "dpkg-query -W -f='`${Status}' zq-ci-dummy 2>/dev/null || echo gone") -notmatch 'install ok installed') 'package still installed'
                # Tamper protection: removal needs a code.
                $deadline = (Get-Date).AddMinutes(2)
                do { Start-Sleep 5; $p = Wsl 'zentriq-agent --protection' } while ($p -notmatch 'Tamper protection: on' -and (Get-Date) -lt $deadline)
                Assert ($p -match 'Tamper protection: on') "protection: $p"
                $refused = Wsl 'apt-get remove -y zentriq-agent >/tmp/rm.log 2>&1 && echo REMOVED || echo REFUSED; grep -m1 "is protected" /tmp/rm.log || tail -5 /tmp/rm.log'
                Assert ($refused -match 'REFUSED' -and $refused -match 'protected') "removal without a code: $refused"
                $code = Api POST '/agents/uninstall-code' @{} -Session $s
                $deadline = (Get-Date).AddMinutes(2)
                do { Start-Sleep 5; $rm = Wsl "pkill -f 'zentriq-agent --run' || true; zentriq-agent --uninstall $($code.code) 2>&1 || true; test -e /opt/zentriq-agent/zentriq-agent && echo STILL || echo GONE" } while ($rm -notmatch 'GONE' -and (Get-Date) -lt $deadline)
                Assert ($rm -match 'GONE') "removal with the code: $rm"
                "$($os.name) $($os.version) · $($os.editionId) · $($os.installationType) · packages listed · $($lin.Count) Linux checks · live connection · package removed via console · removal refused without a code, done with it"
            } finally {
                Get-Content (Join-Path $OutDir 'linux-agent-wsl.log') -ErrorAction SilentlyContinue | Out-Null
                try { & wsl.exe -d $wslDistro -u root -- sh -c 'cp /var/log/zentriq-agent/*.log /var/log/zentriq-agent/run.out /tmp/ 2>/dev/null; cat /var/log/zentriq-agent/agent.log 2>/dev/null' 2>$null | Set-Content (Join-Path $OutDir 'linux-agent.log') } catch { }
                Api PUT '/agents/settings' @{ checkinMinutes = 30; inventoryMinutes = 180; uninstallProtection = $false; autoUpdate = $false; autoUpdateMax = 25 } -Session $s | Out-Null
            }
        } | Out-Null
    } else { Record 'T81' 'Linux agent end to end' 'BLOCKED' "WSL distribution '$wslDistro' not available on this runner" }

    Check 'T14' 'Logs contain no administrator password or connection-string passwords' {
        $paths = @((Join-Path $dataDir 'Logs'), $env:TEMP, $OutDir, (Join-Path $env:SystemRoot 'Temp'))
        $files = $paths | Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem $_ -Recurse -File -Include '*.log', '*.txt', '*.json' -ErrorAction SilentlyContinue }
        $hits = @($files | Select-String -SimpleMatch $adminPassword -List -ErrorAction SilentlyContinue)
        if ($hits.Count -gt 0) { throw "administrator password found in: $(($hits | ForEach-Object Path) -join ', ')" }
        $hits = @($files | Select-String -SimpleMatch $scanPassword -List -ErrorAction SilentlyContinue)
        if ($hits.Count -gt 0) { throw "scan credential password found in: $(($hits | ForEach-Object Path) -join ', ')" }
        $cs = $files | Select-String -Pattern '(?i)(password|pwd)\s*=\s*(?!\*\*\*REDACTED)[^;\s"]{6,}' -List -ErrorAction SilentlyContinue | Where-Object { $_.Line -notmatch 'PasswordPolicy|password=\*\*\*' }
        $cs = @($cs)
        if ($cs.Count -gt 0) { throw "possible secret in: $(($cs | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }) -join ' | ')" }
        "$(@($files).Count) log files scanned"
    } | Out-Null

    $uninstalled = Check 'T15' 'Uninstall removes the program but keeps customer data' {
        Run-Setup $(if ($UpgradeBundle) { $UpgradeBundle } else { $Bundle }) @('/uninstall') 'uninstall' | Out-Null
        Assert (-not (Get-Service ZentriqApi -ErrorAction SilentlyContinue)) 'ZentriqApi service still registered'
        Assert (-not (Test-Path $ctl)) 'program files still present'
        Assert (-not (Test-Path (Join-Path $env:PUBLIC 'Desktop\Zentriq Web Console.url'))) 'desktop shortcut not removed'
        Assert (Test-Path (Join-Path $dataDir 'Secrets\app.secrets')) 'secrets were deleted'
        Assert (Test-Path (Join-Path $dataDir 'Config\zentriq.json')) 'configuration was deleted'
        Assert ((Get-Service 'postgresql-x64-17').Status -eq 'Running') 'PostgreSQL was removed or stopped'
        Assert (Get-ChildItem (Join-Path $dataDir 'Backups') -Filter '*.zbak') 'backups were deleted'
        'program removed; data, backups and PostgreSQL kept'
    }
    if ($uninstalled) {
        Check 'T16' 'Reinstall reuses existing data without resetting the trial' {
            Run-Setup $(if ($UpgradeBundle) { $UpgradeBundle } else { $Bundle }) @() 'reinstall' | Out-Null
            Wait-Ready | Out-Null
            Assert ((Setup-Status).trialExpiresAt -eq $script:trialEnd) 'trial was reset'
            Login | Out-Null
            'data reused; trial unchanged; sign-in works'
        } | Out-Null
    } else { Record 'T16' 'Reinstall after uninstall' 'BLOCKED' 'uninstall failed' }
}

foreach ($t in @(
    @('T17', 'Restart of Windows (trial persists across reboot)', 'CI runners cannot reboot mid-job; covered by T09/T10/T16 persistence checks. Run manually on a VM.'),
    @('T18', 'Trial expiry enforcement end-to-end (clock moved past 14 days)', 'Covered by unit tests (LicenseEvaluator); not run against an installed server.'),
    @('T19', 'Valid commercial license activation end-to-end', 'Needs the production license server and key. Online/offline activation, check-ins, suspension, revocation, renewal and support are covered end to end by the license server tests in ci-quick (verified with the product verifier).'),
    @('T20', 'AD/OU scan and directory sync (computers, users, groups, OUs) in a test domain', 'No Active Directory domain on CI runners. T37 covers validation and error handling only.'),
    @('T38', 'Password reset by e-mail (SMTP delivery and link)', 'No mail server on CI runners; T36 covers the no-SMTP path and zentriqctl reset-password.'),
    @('T39', 'Setup refuses Windows Server 2012 R2 and older', 'CI has only Windows Server 2022; the bundle/MSI conditions need a manual run on an old VM.'),
    @('T40', 'Installation on Windows Server 2016', 'CI has only Windows Server 2022. Run manually on a Server 2016 VM.'),
    @('T30', 'Agentless SSH (Linux) and SNMP collection against real devices', 'No Linux/SNMP targets on the Windows CI runner; parsers covered by unit tests. Run manually.'),
    @('T31', 'Agent over the internet through a firewall/reverse proxy', 'CI runs agent and server on one machine. Run manually with a published port 8444.'),
    @('T21', 'Interactive wizard (UI) walk-through', 'Silent install exercises the same provisioning; UI pages need a manual run on a VM.'))) {
    Record $t[0] $t[1] 'NOT RUN' $t[2]
}

# ---- report ------------------------------------------------------------------------------------
$environment = [ordered]@{ os = "$($os.Caption) $($os.Version)"; machine = $env:COMPUTERNAME; bundle = (Split-Path $Bundle -Leaf); upgradeBundle = $UpgradeBundle; finishedUtc = (Get-Date).ToUniversalTime().ToString('o') }
[ordered]@{ environment = $environment; results = $results } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'installer-test-results.json') -Encoding UTF8
$md = @("# Zentriq installer acceptance results", '', "Environment: $($environment.os) ($($environment.machine)), $($environment.finishedUtc)", '', '| ID | Test | Result | Detail |', '|---|---|---|---|')
$md += $results | ForEach-Object { "| $($_.id) | $($_.test) | **$($_.outcome)** | $(($_.detail -replace '\|', '/') -replace "`r?`n", ' ') |" }
$md | Set-Content (Join-Path $OutDir 'installer-test-results.md') -Encoding UTF8

# The agent's own log (application control, deployments, tamper protection).
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir 'agent-logs') | Out-Null
Get-ChildItem 'C:\ProgramData\Zentriq\Agent\Logs' -ErrorAction SilentlyContinue | Copy-Item -Destination (Join-Path $OutDir 'agent-logs') -ErrorAction SilentlyContinue

# Collect logs for the artifact (secrets files are never copied).
$logOut = Join-Path $OutDir 'logs'
New-Item -ItemType Directory -Force -Path $logOut | Out-Null
Copy-Item (Join-Path $dataDir 'Logs\*') $logOut -Recurse -ErrorAction SilentlyContinue
Get-ChildItem $env:TEMP -Filter 'Zentriq*.log' -ErrorAction SilentlyContinue | Copy-Item -Destination $logOut
Get-ChildItem $env:TEMP -Filter 'install-postgresql.log' -ErrorAction SilentlyContinue | Copy-Item -Destination $logOut

$failed = @($results | Where-Object outcome -eq 'FAIL').Count
Write-Host "`n$(@($results | Where-Object outcome -eq 'PASS').Count) passed, $failed failed, $(@($results | Where-Object outcome -eq 'BLOCKED').Count) blocked, $(@($results | Where-Object outcome -eq 'NOT RUN').Count) not run"
if ($failed -gt 0 -or @($results | Where-Object outcome -eq 'BLOCKED').Count -gt 0) { exit 1 }
exit 0   # explicit: native tools run by the tests (e.g. ZentriqAgent.exe --status after revocation) leave $LASTEXITCODE set
