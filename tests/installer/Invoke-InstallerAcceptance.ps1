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
$installed = Check 'T01' 'First-time installation (silent, answer file)' { $sw = [Diagnostics.Stopwatch]::StartNew(); $r = Run-Setup $Bundle @("ZQ_ANSWERFILE=`"$answer`"") 'install'; "$r in $([int]$sw.Elapsed.TotalMinutes) min" }
Remove-Item $answer -Force   # contains the administrator password

if (-not $installed) {
    foreach ($t in 'T02 PostgreSQL provisioning', 'T03 Database migration', 'T04 Administrator sign-in', 'T05 API and worker services', 'T06 Trial initialization',
                   'T07 HTTPS', 'T08 zentriqctl verify', 'T09 Restart services', 'T10 Repair', 'T11 Backup', 'T12 Tampered license', 'T13 Upgrade', 'T14 Logs free of credentials', 'T15 Uninstall', 'T16 Reinstall', 'T22 Shortcuts', 'T23 Web console', 'T24 Trial policy', 'T25 Company profile', 'T26 Agent install', 'T27 Agent port', 'T28 Agentless WinRM', 'T29 Agent revoke/uninstall', 'T32 Leftover data folder', 'T33 Show enrollment code', 'T34 Delete scans', 'T35 MFA', 'T36 Password reset', 'T37 AD connection', 'T41 Asset type and IPv4', 'T42 Collect all', 'T43 Service via agent', 'T44 Service agentless', 'T45 Software uninstall', 'T46 Drill-down and offline', 'T47 Agent update from the console', 'T48 Reports', 'T49 Vulnerability management', 'T50 Windows vulnerabilities and workflow', 'T51 Live Microsoft data', 'T52 Report analytics and PDF', 'T53 Security configuration', 'T54 Integrations', 'T55 API tokens', 'T56 Departments', 'T57 Change history', 'T58 Company logo', 'T59 Remote support', 'T60 Remote support via agent', 'T61 Certificate follows the server', 'T62 Devices without an agent', 'T63 Network inventory', 'T64 Non-IT assets', 'T65 Remote quick support', 'T66 Non-IT groups and departments', 'T67 Agent uninstall protection', 'T68 Live agent connection', 'T69 Locations and IT accessories') {
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
        "index + $js ($([int]($bundle.RawContentLength/1KB)) KB)"
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
            $a = @(Api GET '/agents' -Session $s) | Where-Object { $_.assetId -and $_.lastInventoryAt } | Select-Object -First 1
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
        Check 'T67' 'Agent uninstall protection: removal needs a one-hour code from the console; service cannot be stopped; reinstall' {
            $s = Login
            $exe = Join-Path $env:ProgramFiles 'Zentriq Agent\ZentriqAgent.exe'
            Api PUT '/agents/settings' @{ checkinMinutes = 30; inventoryMinutes = 180; uninstallProtection = $true } -Session $s | Out-Null
            # The agent service (SYSTEM) turns protection on at its next check-in (every 2 minutes here).
            $deadline = (Get-Date).AddMinutes(5)
            do { Start-Sleep 10; $reg = Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent' } while (-not $reg.PSObject.Properties['UninstallProtected'] -and (Get-Date) -lt $deadline)
            Assert ($reg.PSObject.Properties['UninstallProtected'] -and $reg.UninstallProtected -eq 1) 'agent did not turn uninstall protection on'
            $readable = $true; try { Get-ItemProperty 'HKLM:\SOFTWARE\Zentriq\Agent\Protection' -ErrorAction Stop | Out-Null } catch { $readable = $false }
            Assert (-not $readable) 'the uninstall key is readable by administrators'
            $stopped = $true; try { Stop-Service ZentriqAgent -ErrorAction Stop } catch { $stopped = $false }
            Assert (-not $stopped -and (Get-Service ZentriqAgent).Status -eq 'Running') 'an administrator could stop the protected agent service'
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
            "no code: exit $($p1.ExitCode); wrong code: exit $($p2.ExitCode); service stop refused; code for all agents $($code.code) (valid until $($code.expiresAt)) removed the agent, the replaced code did not; reinstalled and protection switched off"
        } | Out-Null
    } else { Record 'T67' 'Agent uninstall protection' 'BLOCKED' 'agent install failed' }

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

    Check 'T34' 'Scan history can be deleted (single and by age); assets are kept' {
        $s = Login
        $ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -match '^(10\.|172\.(1[6-9]|2\d|3[01])\.|192\.168\.)' -and $_.PrefixOrigin -ne 'WellKnown' } | Select-Object -First 1).IPAddress
        $scan = Api POST '/discovery/scans' @{ name = 'ci-delete-me'; scanType = 'single'; targetsText = $ip } -Session $s
        $deadline = (Get-Date).AddMinutes(3)
        do { Start-Sleep 3; $d = Api GET "/discovery/scans/$($scan.id)" -Session $s } while ($d.scan.status -in 'queued', 'running' -and (Get-Date) -lt $deadline)
        $r = Invoke-WebRequest "$base/api/v1/discovery/scans/$($scan.id)" -Method Delete -WebSession $s -Headers @{ 'X-Zentriq-Request' = '1' } -SkipHttpErrorCheck
        Assert ($r.StatusCode -eq 204) "delete returned $($r.StatusCode)"
        $g = Invoke-WebRequest "$base/api/v1/discovery/scans/$($scan.id)" -WebSession $s -SkipHttpErrorCheck
        Assert ($g.StatusCode -eq 404) "deleted scan still readable ($($g.StatusCode))"
        $bulk = Api POST '/discovery/scans/delete' @{ olderThanDays = 3650 } -Session $s
        Assert ($null -ne $bulk.deleted) 'bulk delete returned no count'
        $assets = (Api GET '/assets/summary' -Session $s).total
        "single delete 204, then 404; bulk delete ok ($($bulk.deleted)); assets kept ($assets)"
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
        Assert (@($rules).Count -eq 30 -and @($rules | Where-Object enabled).Count -eq 28) "rules: $(@($rules).Count), on $(@($rules | Where-Object enabled).Count)"
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

    Check 'T69' 'Locations and IT accessories: location on devices and hardware inventory; receive, issue, return, transfer; no negative stock; holdings; reorder alert' {
        $s = Login
        function Raw([string] $Method, [string] $Path, $Body) {
            $p = @{ Uri = "$base/api/v1$Path"; Method = $Method; WebSession = $s; Headers = @{ 'X-Zentriq-Request' = '1' }; ContentType = 'application/json'; SkipHttpErrorCheck = $true }
            if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6) }
            Invoke-WebRequest @p
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
        "location assigned and filtered ($hwNote); received 10, issued 6 to CI Person (stock 4, below reorder 5), issue of 5 refused (Only 4), returned 1 faulty, transferred 2; $moves movements; location and accessory with history protected"
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
        } else { Record 'T47' 'Agent update from the console' 'BLOCKED' 'agent package from T26 not available' }
    } else {
        Record 'T13' 'Upgrade to the next release' 'NOT RUN' 'no -UpgradeBundle supplied'
        Record 'T47' 'Agent update from the console' 'NOT RUN' 'needs -UpgradeBundle (a newer agent package on the server)'
    }

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
    @('T19', 'Valid commercial license activation end-to-end', 'Requires the vendor private key; signer + verifier covered by unit tests. Run manually with docs/licensing.md.'),
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
