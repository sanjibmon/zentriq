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
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $body = @{ email = $adminEmail; password = $adminPassword } | ConvertTo-Json
    $r = Invoke-WebRequest "$base/api/v1/auth/login" -Method Post -Body $body -ContentType 'application/json' -Headers @{ 'X-Zentriq-Request' = '1' } -WebSession $s
    Assert ($r.StatusCode -eq 200) "login returned $($r.StatusCode)"
    return $s
}
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

# ---- T01 install -------------------------------------------------------------------------------
$installed = Check 'T01' 'First-time installation (silent, answer file)' { $sw = [Diagnostics.Stopwatch]::StartNew(); $r = Run-Setup $Bundle @("ZQ_ANSWERFILE=`"$answer`"") 'install'; "$r in $([int]$sw.Elapsed.TotalMinutes) min" }
Remove-Item $answer -Force   # contains the administrator password

if (-not $installed) {
    foreach ($t in 'T02 PostgreSQL provisioning', 'T03 Database migration', 'T04 Administrator sign-in', 'T05 API and worker services', 'T06 Trial initialization',
                   'T07 HTTPS', 'T08 zentriqctl verify', 'T09 Restart services', 'T10 Repair', 'T11 Backup', 'T12 Tampered license', 'T13 Upgrade', 'T14 Logs free of credentials', 'T15 Uninstall', 'T16 Reinstall', 'T22 Shortcuts', 'T23 Web console', 'T24 Trial policy', 'T25 Company profile', 'T26 Agent install', 'T27 Agent port', 'T28 Agentless WinRM', 'T29 Agent revoke/uninstall') {
        Record $t.Substring(0, 3) $t.Substring(4) 'BLOCKED' 'installation failed'
    }
} else {
    Check 'T02' 'PostgreSQL provisioned as a Windows service' {
        $svc = Get-Service 'postgresql-x64-17'; Assert ($svc.Status -eq 'Running') "status $($svc.Status)"
        $listen = Get-NetTCPConnection -LocalPort 5432 -State Listen; Assert (-not ($listen | Where-Object { $_.LocalAddress -notin '127.0.0.1', '::1' })) 'PostgreSQL listens on a non-loopback address'
        'running, loopback-only'
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
        $ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -match '^(10\.|172\.(1[6-9]|2\d|3[01])\.|192\.168\.)' -and $_.PrefixOrigin -ne 'WellKnown' } | Select-Object -First 1).IPAddress
        Assert $ip 'no private IPv4 address on this machine'
        $s = Login
        $before = (Api GET '/assets/summary' -Session $s).total
        $cred = Api POST '/credentials' @{ name = 'ci-local-admin'; kind = 'windows_local'; username = "$env:COMPUTERNAME\$scanUser"; secret = @{ password = $scanPassword } } -Session $s
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
    } else { Record 'T13' 'Upgrade to the next release' 'NOT RUN' 'no -UpgradeBundle supplied' }

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
    @('T20', 'AD/OU scan in a test domain', 'No Active Directory domain on CI runners.'),
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
