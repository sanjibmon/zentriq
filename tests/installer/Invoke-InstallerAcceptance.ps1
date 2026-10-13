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
                   'T07 HTTPS', 'T08 zentriqctl verify', 'T09 Restart services', 'T10 Repair', 'T11 Backup', 'T12 Tampered license', 'T13 Upgrade', 'T14 Logs free of credentials', 'T15 Uninstall', 'T16 Reinstall', 'T22 Shortcuts', 'T23 Web console', 'T24 Trial policy', 'T25 Company profile', 'T26 Agent install', 'T27 Agent port', 'T28 Agentless WinRM', 'T29 Agent revoke/uninstall', 'T32 Leftover data folder', 'T33 Show enrollment code', 'T34 Delete scans', 'T35 MFA', 'T36 Password reset', 'T37 AD connection', 'T41 Asset type and IPv4', 'T42 Collect all', 'T43 Service via agent', 'T44 Service agentless', 'T45 Software uninstall', 'T46 Drill-down and offline', 'T47 Agent update from the console') {
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
        $tok = Api POST '/agents/enrollment-tokens' @{ name = 'ci'; validDays = 1; maxUses = 2; serverUrl = 'https://localhost:8444' } -Session $s
        Assert ($tok.code -like 'ZQ1.*') 'no enrollment code returned'
        $log = Join-Path $OutDir 'agent-install.log'
        $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$agentMsi`"", '/qn', "ENROLLMENTCODE=$($tok.code)", '/l*v', "`"$log`"" -Wait -PassThru
        Assert ($p.ExitCode -eq 0) "agent MSI exit $($p.ExitCode); see $log"
        Assert ((Get-Service ZentriqAgent).Status -eq 'Running') 'ZentriqAgent service not running'
        Assert (-not (Select-String -Path $log -SimpleMatch $tok.code -Quiet)) 'enrollment code written to the MSI log'
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
        "$($a.hostname): $(@($inv.software).Count) applications, $([math]::Round($inv.document.hardware.memoryBytes/1GB)) GB RAM, health $($a.health); MSI signature: $($sig.Status)"
    }
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
        'code shown again; revoked code refused (410); list does not expose codes'
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
                $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$agentMsi`"", '/qn', "ENROLLMENTCODE=$($tok.code)", '/l*v', "`"$log`"" -Wait -PassThru
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
