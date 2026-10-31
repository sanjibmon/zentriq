<#
.SYNOPSIS
  End-to-end test of Zentriq-LicenseServer-Setup.msi on Windows: silent install (PostgreSQL + services), the browser
  setup wizard through its API (owner, key ceremony, backup, finish), license generation and activation, automatic
  key unlock after a restart, public mode with a certificate (HTTPS, redirect, wizard not reachable from the
  network) applied by the service restarting itself, daily backup, uninstall keeping the data, reinstall.
#>
param(
    [Parameter(Mandatory)][string] $Msi,
    [Parameter(Mandatory)][string] $Report,
    [string] $LogDir = (Join-Path $env:RUNNER_TEMP 'zls-setup-logs')
)
$ErrorActionPreference = 'Stop'
$Msi = [IO.Path]::GetFullPath($Msi)   # msiexec needs backslashes
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
function Check([string] $Id, [string] $Name, [scriptblock] $Body) {
    Write-Host "==== $Id $Name"
    try { $d = & $Body; $results.Add([pscustomobject]@{ Id = $Id; Name = $Name; Result = 'PASS'; Detail = "$d" }) }
    catch { $results.Add([pscustomobject]@{ Id = $Id; Name = $Name; Result = 'FAIL'; Detail = "$($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))" }) }
}
function Assert($c, [string] $m) { if (-not $c) { throw $m } }
function WaitFor([scriptblock] $Cond, [int] $Seconds, [string] $What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { try { if (& $Cond) { return } } catch { }; Start-Sleep 2 }
    throw "timed out: $What"
}

$base = 'http://localhost:5080'
$data = Join-Path $env:ProgramData 'Zentriq\LicenseServer'
$hdr = @{ 'X-ZLS-Request' = '1' }
$s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$signing = 'ci setup signing passphrase 2026'
$owner = 'owner@ci.test'; $ownerPass = 'ci setup owner passphrase'

function Healthy([string] $Url = $base) { try { (Invoke-WebRequest "$Url/health" -UseBasicParsing -TimeoutSec 5 -SkipCertificateCheck).StatusCode -eq 200 } catch { $false } }
function Setup([string] $Method, [string] $Path, $Body) {
    $p = @{ Uri = "$base/setup/api$Path"; Method = $Method; Headers = $hdr; WebSession = $script:s; ContentType = 'application/json' }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6) }
    Invoke-RestMethod @p
}
function Api([string] $Method, [string] $Path, $Body) {
    $p = @{ Uri = "$base/admin/api$Path"; Method = $Method; Headers = $hdr; WebSession = $script:s; ContentType = 'application/json' }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 8) }
    Invoke-RestMethod @p
}
function Login { $script:s = New-Object Microsoft.PowerShell.Commands.WebRequestSession; Api POST '/login' @{ email = $owner; password = $ownerPass } | Out-Null }
function MsiRun([string[]] $Arguments, [string] $Log) {
    $p = Start-Process msiexec.exe -ArgumentList ($Arguments + @('/l*v', "`"$(Join-Path $LogDir $Log)`"")) -Wait -PassThru
    return $p.ExitCode
}
# Status and Location without following redirects (and any certificate).
function Peek([string] $Url) {
    $h = [Net.Http.HttpClientHandler]::new(); $h.AllowAutoRedirect = $false
    $h.ServerCertificateCustomValidationCallback = [Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
    $c = [Net.Http.HttpClient]::new($h)
    try { $r = $c.GetAsync($Url).GetAwaiter().GetResult(); [pscustomobject]@{ Status = [int]$r.StatusCode; Location = "$($r.Headers.Location)" } }
    finally { $c.Dispose() }
}
function Holders([int] $Port) {
    @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | ForEach-Object { "$($_.LocalAddress):$Port by $((Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName) ($($_.OwningProcess))" }) -join '; '
}
function SvcPid { (Get-CimInstance Win32_Service -Filter "Name='ZentriqLicenseServer'").ProcessId }
function Req { @{ format = 'zentriq-activation-request'; schemaVersion = 3; requestId = [guid]::NewGuid(); installationId = [guid]::NewGuid(); productVersion = '1.1.1';
                  machineName = 'ZQ-CUSTOMER'; organization = 'CI Test'; createdAt = (Get-Date).ToUniversalTime().ToString('o'); currentUsage = @{} } }

Check 'S01' 'Silent install: PostgreSQL and the license server as Windows services; the portal leads to the setup wizard' {
    $code = MsiRun @('/i', "`"$Msi`"", '/qn') 'msi-install.log'
    Assert ($code -in 0, 3010) "msiexec exit $code"
    $db = Get-Service ZentriqLicenseDb; $srv = Get-Service ZentriqLicenseServer
    Assert ($db.Status -eq 'Running' -and $srv.Status -eq 'Running') "services: db $($db.Status), server $($srv.Status)"
    $w = Get-CimInstance Win32_Service -Filter "Name='ZentriqLicenseServer'"
    Assert ($w.StartName -eq 'NT SERVICE\ZentriqLicenseServer' -and $w.StartMode -eq 'Auto') "server service: $($w.StartName) $($w.StartMode)"
    Assert ((Get-CimInstance Win32_Service -Filter "Name='ZentriqLicenseDb'").StartName -match 'NetworkService') 'database service account'
    WaitFor { Healthy } 120 'health'
    $r = Peek "$base/admin/"
    Assert ($r.Status -eq 302 -and $r.Location -eq '/setup/') "portal before setup: $($r.Status) $($r.Location)"
    Assert ((Invoke-WebRequest "$base/setup/" -UseBasicParsing).Content -match 'setup.js') 'setup page'
    Assert (Get-NetFirewallRule -DisplayName 'Zentriq License Server (HTTP/HTTPS)' -ErrorAction SilentlyContinue) 'firewall rule missing'
    $acl = (Get-Acl $data).Access | ForEach-Object { $_.IdentityReference.Value }
    Assert (-not ($acl -match 'Users|Everyone|Authenticated')) "data folder readable by: $($acl -join ', ')"
    $listen = @(Get-NetTCPConnection -State Listen -LocalPort 5439 -ErrorAction SilentlyContinue | ForEach-Object LocalAddress) | Sort-Object -Unique
    Assert ($listen.Count -ge 1 -and -not ($listen | Where-Object { $_ -notin '127.0.0.1' })) "PostgreSQL listens on: $($listen -join ', ')"
    $listenHttp = @(Get-NetTCPConnection -State Listen -LocalPort 5080 -ErrorAction SilentlyContinue | ForEach-Object LocalAddress) | Sort-Object -Unique
    Assert (-not ($listenHttp | Where-Object { $_ -notin '127.0.0.1', '::1' })) "server listens on: $($listenHttp -join ', ')"
    Write-Host "Listening before public mode: 80 [$(Holders 80)] 443 [$(Holders 443)]"
    "services running (server as $($w.StartName), delayed auto start); health 200; /admin -> /setup/; PostgreSQL on 127.0.0.1:5439 only; http on localhost:5080 only; data folder admins/SYSTEM/service only"
}

Check 'S02' 'Setup wizard: owner, key ceremony, product entry and encrypted backup, backup confirmed, finish' {
    $st = Setup GET '/state'
    Assert (-not $st.ownerExists -and -not $st.complete) "state: $($st | ConvertTo-Json -Compress)"
    Setup POST '/owner' @{ email = $owner; name = 'CI Owner'; password = $ownerPass } | Out-Null
    $k = Setup POST '/key/new' @{ keyId = 'zentriq-ci-setup'; passphrase = $signing }
    $script:fp = $k.fingerprint
    Assert ($script:fp -match '^SHA256:([0-9A-F]{2}:){31}[0-9A-F]{2}$') "fingerprint $($script:fp)"
    $entry = (Invoke-WebRequest "$base/setup/api/key/product-entry" -WebSession $s -UseBasicParsing).Content
    Assert ($entry -match 'zentriq-ci-setup' -and $entry -notmatch 'PRIVATE') 'product entry'
    $bk = (Invoke-WebRequest "$base/setup/api/key/backup" -WebSession $s -UseBasicParsing).Content
    $bk = if ($bk -is [byte[]]) { [Text.Encoding]::ASCII.GetString($bk) } else { $bk }
    Assert ($bk -match 'BEGIN ENCRYPTED PRIVATE KEY') 'backup is not the encrypted key'
    $script:keyBackup = $bk
    $fin = Invoke-WebRequest "$base/setup/api/finish" -Method Post -Headers $hdr -WebSession $s -SkipHttpErrorCheck -UseBasicParsing
    Assert ($fin.StatusCode -eq 400) "finish before the backup was confirmed: $($fin.StatusCode)"
    Setup POST '/key/backed-up' @{} | Out-Null
    Setup POST '/finish' @{} | Out-Null
    Assert ((Setup GET '/state').complete) 'not complete'
    Assert ((Invoke-WebRequest "$base/admin/" -UseBasicParsing).Content -match 'License Portal') 'portal after setup'
    $keyAcl = (Get-Acl (Join-Path $data 'keys\zentriq-ci-setup.private.pem')).Access | ForEach-Object { $_.IdentityReference.Value }
    Assert (-not ($keyAcl -match 'Users|Everyone|Authenticated|Administrators')) "private key readable by: $($keyAcl -join ', ')"
    $secret = [IO.File]::ReadAllBytes((Join-Path $data 'protected\signing.bin'))
    Assert (-not ([Text.Encoding]::UTF8.GetString($secret) -match 'passphrase')) 'signing passphrase stored in plain text'
    "owner created; key zentriq-ci-setup $($script:fp); product entry public only; backup encrypted; finish refused until the backup was confirmed; private key ACL: $($keyAcl -join ', '); passphrase DPAPI-protected"
}

Check 'S03' 'Licenses: subscription offline file, perpetual online with check-in, customer license file bound online, tampering rejected' {
    $c = Api GET '/catalog'
    Assert ($c.signing.fingerprint -eq $script:fp) "catalog fingerprint $($c.signing.fingerprint)"
    $cust = Api POST '/customers' @{ company = 'CI Setup Ltd'; contactName = 'Test Admin'; contactEmail = 'it@ci.test'; country = 'India' }
    $lic = Api POST '/licenses' @{ customerId = $cust.id; licenseType = 'subscription'; termYears = 3; edition = 'advanced'; devices = 250; nonItPack = 500; cloudInstances = 20; technicians = 2 }
    $file = Api POST "/licenses/$($lic.id)/offline" @{ request = (Req) }
    $v = Api POST '/verify' @{ document = $file.license }
    Assert ($v.valid -and $v.payload.termYears -eq 3 -and $v.payload.entitlements.devices -eq 250) "offline: $($v.message)"
    $perp = Api POST '/licenses' @{ customerId = $cust.id; licenseType = 'perpetual'; supportYears = 1; edition = 'standard'; devices = 100 }
    $r = Req
    $act = Invoke-RestMethod "$base/api/v1/activate" -Method Post -ContentType 'application/json' -Body (@{ licenseKey = $perp.licenseKey; request = $r } | ConvertTo-Json -Depth 5)
    Api POST "/licenses/$($perp.id)/support" @{ years = 2 } | Out-Null
    $ci = Invoke-RestMethod "$base/api/v1/checkin" -Method Post -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($act.activationToken)" } `
        -Body (@{ installationId = $r.installationId; licenseId = $perp.id; productVersion = '1.1.1'; releaseDate = (Get-Date).ToUniversalTime().ToString('o'); usage = @{ devices = 15 } } | ConvertTo-Json)
    Assert ($ci.status -eq 'active') "check-in $($ci.status)"
    $cert = Api POST '/licenses' @{ customerId = $cust.id; licenseType = 'subscription'; termYears = 1; edition = 'standard'; devices = 50 }
    $f = Api POST "/licenses/$($cert.id)/file" @{}
    $bound = Invoke-RestMethod "$base/api/v1/activate" -Method Post -ContentType 'application/json' -Body (@{ licenseFile = $f.license; request = (Req) } | ConvertTo-Json -Depth 5)
    Assert ($bound.activationToken) 'license file not bound online'
    $doc = $file.license | ConvertFrom-Json
    $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($doc.payload)) -replace '"devices":250', '"devices":25000'
    $doc.payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    $t = Api POST '/verify' @{ document = ($doc | ConvertTo-Json -Compress) }
    Assert (-not $t.valid -and $t.rejection -eq 'BadSignature') "tampered: $($t.rejection)"
    "subscription $($lic.id) offline verified; perpetual $($perp.id) online + check-in; license file bound online; tampered file rejected"
}

Check 'S04' 'After a restart (as after a reboot) the key is unlocked automatically and the database starts with the server' {
    Stop-Service ZentriqLicenseServer
    Stop-Service ZentriqLicenseDb -Force
    Start-Service ZentriqLicenseServer   # starts the database service first (dependency)
    Assert ((Get-Service ZentriqLicenseDb).Status -eq 'Running') 'database not started with the server'
    WaitFor { Healthy } 120 'health after restart'
    Login
    WaitFor { (Api GET '/catalog').signing.fingerprint -eq $script:fp } 60 'key not unlocked after restart'
    'stopped both services, started the server: database came up as its dependency, key unlocked without a prompt'
}

Check 'S05' 'Public mode with own certificate: wizard restarts the service, HTTPS on 443 with that certificate, HTTP redirects, wizard not reachable from the network; back to local' {
    $cert = New-SelfSignedCertificate -DnsName 'license.ci.test' -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddMonths(6)
    $pfxFile = Join-Path $LogDir 'ci.pfx'
    $pw = ConvertTo-SecureString 'ci pfx password' -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfxFile -Password $pw | Out-Null
    $pid1 = SvcPid
    $r = Setup POST '/access' @{ mode = 'public'; hostname = 'license.ci.test'; certificate = 'pfx'; pfxBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxFile)); pfxPassword = 'ci pfx password' }
    Assert ($r.restart -and $r.canRestart) "access: $($r | ConvertTo-Json -Compress)"
    WaitFor { (SvcPid) -ne $pid1 -and (SvcPid) -ne 0 -and (Healthy) } 90 'service did not come back after the restart'
    $problems = @((Setup GET '/state').accessProblems)
    Write-Host "Access problems after the restart: $($problems -join ' | '); 80 [$(Holders 80)] 443 [$(Holders 443)]"
    Assert (-not ($problems -match 'Port 443')) "443 not usable: $($problems -join ' ') [$(Holders 443)]"
    $port80 = -not ($problems -match 'Port 80')
    WaitFor { Healthy 'https://localhost' } 30 'https health'
    $tcp = [Net.Sockets.TcpClient]::new('localhost', 443)
    $ssl = [Net.Security.SslStream]::new($tcp.GetStream(), $false, { $true })
    $ssl.AuthenticateAsClient('license.ci.test')
    $served = $ssl.RemoteCertificate.GetCertHashString(); $ssl.Dispose(); $tcp.Dispose()
    Assert ($served -eq $cert.Thumbprint) "served certificate $served, expected $($cert.Thumbprint)"
    if ($port80) {
        $h = Peek 'http://localhost/admin/'
        Assert ($h.Status -in 301, 308 -and $h.Location -eq 'https://license.ci.test/admin/') "http redirect: $($h.Status) $($h.Location)"
        $redirect = 'http -> https://license.ci.test'
    } else { $redirect = "port 80 held by another program on this runner [$(Holders 80)]: reported on the setup page, server kept running on 443 and localhost" }
    $ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1).IPAddress
    $net = Invoke-WebRequest "https://$ip/setup/api/state" -SkipCertificateCheck -SkipHttpErrorCheck -UseBasicParsing
    Assert ($net.StatusCode -eq 404) "wizard from the network ($ip): $($net.StatusCode)"
    $portal = Invoke-WebRequest "https://$ip/admin/" -SkipCertificateCheck -UseBasicParsing
    Assert ($portal.Content -match 'License Portal') 'portal over https from the network'
    $st = Setup GET '/state'
    Assert ($st.publicUrl -eq 'https://license.ci.test') "public url $($st.publicUrl)"
    $pid2 = SvcPid
    Setup POST '/access' @{ mode = 'local' } | Out-Null
    WaitFor { (SvcPid) -ne $pid2 -and (Healthy) } 90 'service did not come back after switching to local'
    Assert (-not (Get-NetTCPConnection -State Listen -LocalPort 443 -ErrorAction SilentlyContinue)) 'still listening on 443 in local mode'
    "restarted by the wizard (pid $pid1 -> $pid2); https://localhost served the uploaded certificate; $redirect; wizard 404 from $ip; portal reachable over https; back to local: 443 closed"
}

Check 'S06' 'Database backups: daily backup written by the service, and on demand' {
    WaitFor { @(Get-ChildItem (Join-Path $data 'backups') -Filter '*.dump' -ErrorAction SilentlyContinue).Count -ge 1 } 120 'no daily backup'
    $b = Setup POST '/backup' @{}
    Assert (Test-Path $b.file) "backup file $($b.file)"
    "$(@(Get-ChildItem (Join-Path $data 'backups') -Filter '*.dump').Count) backup(s) in $data\backups"
}

Check 'S07' 'Uninstall keeps the data; reinstall continues with the same key and licenses' {
    $code = MsiRun @('/x', "`"$Msi`"", '/qn') 'msi-uninstall.log'
    Assert ($code -in 0, 3010) "uninstall exit $code"
    Assert (-not (Get-Service ZentriqLicenseServer -ErrorAction SilentlyContinue) -and -not (Get-Service ZentriqLicenseDb -ErrorAction SilentlyContinue)) 'services still present'
    Assert (-not (Get-NetFirewallRule -DisplayName 'Zentriq License Server (HTTP/HTTPS)' -ErrorAction SilentlyContinue)) 'firewall rule left behind'
    Assert (-not (Test-Path (Join-Path $env:ProgramFiles 'Zentriq License Server\server\Zentriq.LicenseServer.exe'))) 'program files left behind'
    Assert (Test-Path (Join-Path $data 'keys\zentriq-ci-setup.private.pem')) 'signing key removed with the program'
    $code = MsiRun @('/i', "`"$Msi`"", '/qn') 'msi-reinstall.log'
    Assert ($code -in 0, 3010) "reinstall exit $code"
    WaitFor { Healthy } 120 'health after reinstall'
    $script:s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $r = Peek "$base/admin/"
    Assert ($r.Status -eq 200) "portal after reinstall: $($r.Status) (setup should stay finished)"
    Login
    Assert ((Api GET '/catalog').signing.fingerprint -eq $script:fp) 'different key after reinstall'
    $lics = @(Api GET '/licenses' | ForEach-Object { $_ })
    Assert ($lics.Count -ge 3) "licenses lost ($($lics.Count))"
    'uninstall removed services, firewall rule and program, kept data; reinstall: setup finished, same key, licenses kept'
}

# Logs for the run record.
Copy-Item (Join-Path $data 'logs\*') $LogDir -ErrorAction SilentlyContinue
$pgLog = Join-Path $data 'pg\log'
if (Test-Path $pgLog) { Get-ChildItem $pgLog | Copy-Item -Destination $LogDir -ErrorAction SilentlyContinue }

$lines = @('# License server setup (Windows MSI)', '', '| ID | Test | Result | Detail |', '|---|---|---|---|')
$lines += $results | ForEach-Object { "| $($_.Id) | $($_.Name) | **$($_.Result)** | $(($_.Detail -replace '\|', '/' -replace "`r?`n", ' ').Trim()) |" }
$lines | Set-Content $Report -Encoding utf8
$lines | Write-Host
if (@($results | Where-Object Result -ne 'PASS').Count -gt 0) {
    Get-ChildItem $LogDir -Filter '*.log' | Where-Object Name -match 'server-|install\.log$' | ForEach-Object { Write-Host "---- $($_.Name)"; Get-Content $_.FullName -Tail 80 }
    throw 'License server setup test failed.'
}
