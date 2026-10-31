<#
.SYNOPSIS
  End-to-end test of the local license server package on Windows: setup (database, key ceremony, owner), start,
  license generation in the portal (subscription and perpetual), offline and online activation, check-in after a
  support extension, verification of the generated files, tamper detection, backup, stop.
  zls.ps1 runs under Windows PowerShell 5.1 (what customers' and Zentriq staff's PCs have).
#>
param(
    [Parameter(Mandatory)][string] $Zip,
    [Parameter(Mandatory)][string] $Report
)
$ErrorActionPreference = 'Stop'
$results = [System.Collections.Generic.List[object]]::new()
function Check([string] $Id, [string] $Name, [scriptblock] $Body) {
    try { $d = & $Body; $results.Add([pscustomobject]@{ Id = $Id; Name = $Name; Result = 'PASS'; Detail = "$d" }) }
    catch { $results.Add([pscustomobject]@{ Id = $Id; Name = $Name; Result = 'FAIL'; Detail = $_.Exception.Message }) }
}
function Assert($c, [string] $m) { if (-not $c) { throw $m } }

$dir = Join-Path $env:RUNNER_TEMP 'zls-local-test'
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
Expand-Archive $Zip -DestinationPath $dir
$root = Join-Path $dir 'ZentriqLicenseServer'
$zls = Join-Path $root 'zls.ps1'
function Zls([string[]] $Arguments) {
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $zls @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "zls $($Arguments -join ' ') exited $LASTEXITCODE`n$out" }
    $out
}
$base = 'http://localhost:5080'
$hdr = @{ 'X-ZLS-Request' = '1' }

$env:ZLS_SIGNING_PASSPHRASE = 'local test signing passphrase 2026'
$env:ZLS_ADMIN_PASSPHRASE = 'local test portal passphrase'

Check 'L01' 'Setup: own PostgreSQL, key ceremony (encrypted key, public entry, record), owner account' {
    $out = Zls @('setup', '-NonInteractive', '-OwnerEmail', 'owner@local.test', '-KeyId', 'zentriq-test-local')
    $key = Get-Content (Join-Path $root 'data\keys\zentriq-test-local.private.pem') -Raw
    Assert ($key -match 'BEGIN ENCRYPTED PRIVATE KEY') 'private key is not encrypted'
    $entry = Get-Content (Join-Path $root 'data\keys\zentriq-test-local.product-entry.json') -Raw
    Assert ($entry -notmatch 'PRIVATE' -and $entry -match 'zentriq-test-local') 'product entry wrong'
    $script:fp = ((Get-Content (Join-Path $root 'data\keys\zentriq-test-local.ceremony.txt') | Select-String 'Fingerprint:').ToString() -split 'Fingerprint:\s*')[1].Trim()
    Assert ($script:fp -match '^SHA256:([0-9A-F]{2}:){31}[0-9A-F]{2}$') "fingerprint: $script:fp"
    Assert ($out -match [regex]::Escape($script:fp)) 'setup did not print the fingerprint'
    $acl = (Get-Acl (Join-Path $root 'data\keys\zentriq-test-local.private.pem')).Access | ForEach-Object { $_.IdentityReference.Value }
    Assert (-not ($acl -match 'Users|Everyone|Authenticated')) "private key readable by: $($acl -join ', ')"
    "key zentriq-test-local, $script:fp; private key encrypted, owner-only ACL ($($acl -join ', '))"
}

# From here on the passphrase comes from the DPAPI-protected copy setup kept for this Windows user.
Remove-Item Env:\ZLS_SIGNING_PASSPHRASE, Env:\ZLS_ADMIN_PASSPHRASE

Check 'L02' 'Start in the background; portal and offline activation page answer' {
    Zls @('start', '-Background', '-NoBrowser', '-NonInteractive') | Out-Null
    Assert ((Invoke-WebRequest "$base/health" -UseBasicParsing).StatusCode -eq 200) 'health'
    Assert ((Invoke-WebRequest "$base/" -UseBasicParsing).Content -match 'Offline activation') 'offline page'
    Assert ((Invoke-WebRequest "$base/admin/" -UseBasicParsing).Content -match 'License Portal') 'portal page'
    'health 200, / and /admin served'
}

$s = $null
function Api([string] $Method, [string] $Path, $Body) {
    $p = @{ Uri = "$base/admin/api$Path"; Method = $Method; Headers = $hdr; WebSession = $script:s; ContentType = 'application/json' }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 8) }
    Invoke-RestMethod @p
}

Check 'L03' 'Owner signs in; the portal shows the same key id and fingerprint as the ceremony record' {
    $r = Invoke-RestMethod "$base/admin/api/login" -Method Post -Headers $hdr -ContentType 'application/json' -SessionVariable sv `
        -Body (@{ email = 'owner@local.test'; password = 'local test portal passphrase' } | ConvertTo-Json)
    $script:s = $sv
    Assert ($r.isOwner) 'not owner'
    $c = Api GET '/catalog'
    Assert ($c.signing.keyId -eq 'zentriq-test-local' -and $c.signing.fingerprint -eq $script:fp) "catalog: $($c.signing.keyId) $($c.signing.fingerprint)"
    "signed in; $($c.signing.keyId) $($c.signing.fingerprint)"
}

Check 'L04' 'Subscription license (3 years): key shown once, offline test file, verified with the entitlements' {
    $cust = Api POST '/customers' @{ company = 'Local Test Ltd'; contactName = 'Test Admin'; contactEmail = 'it@local.test'; country = 'India' }
    $script:cust = $cust.id
    $lic = Api POST '/licenses' @{ customerId = $cust.id; licenseType = 'subscription'; termYears = 3; edition = 'advanced'; devices = 250; nonItPack = 500; cloudInstances = 20; technicians = 2 }
    Assert ($lic.licenseKey -match '^ZQL(-[0-9A-Z]{5}){4}$') "key $($lic.licenseKey)"
    $req = @{ format = 'zentriq-activation-request'; schemaVersion = 3; requestId = [guid]::NewGuid(); installationId = [guid]::NewGuid(); productVersion = 'test';
              machineName = 'TEST-REQUEST'; organization = 'Local Test'; createdAt = (Get-Date).ToUniversalTime().ToString('o'); currentUsage = @{} }
    $file = Api POST "/licenses/$($lic.id)/offline" @{ request = $req }
    $v = Api POST '/verify' @{ document = $file.license }
    Assert ($v.valid -and $v.currentlyValid) "verify: $($v.message)"
    $p = $v.payload
    Assert ($p.licenseType -eq 'subscription' -and $p.termYears -eq 3 -and $p.edition -eq 'advanced' -and $p.activation -eq 'offline') "payload: $($p | ConvertTo-Json -Compress)"
    Assert ($p.entitlements.devices -eq 250 -and $p.entitlements.nonit_assets -eq 500 -and $p.entitlements.cloud_instances -eq 20 -and $p.entitlements.technicians -eq 2) "entitlements: $($p.entitlements | ConvertTo-Json -Compress)"
    Assert ($p.licensee.company -eq 'Local Test Ltd') 'licensee'
    $script:subDoc = $file.license
    "$($lic.id): $($file.fileName) verified; ends $(([datetime]$p.expiresAt).ToString('yyyy-MM-dd'))"
}

Check 'L05' 'Perpetual license: online activation, support extension arrives with the next check-in' {
    $lic = Api POST '/licenses' @{ customerId = $script:cust; licenseType = 'perpetual'; supportYears = 1; edition = 'standard'; devices = 100 }
    $install = [guid]::NewGuid()
    $req = @{ format = 'zentriq-activation-request'; schemaVersion = 3; requestId = [guid]::NewGuid(); installationId = $install; productVersion = '1.1.1';
              machineName = 'ZQ-LOCAL'; organization = 'Local Test'; createdAt = (Get-Date).ToUniversalTime().ToString('o'); currentUsage = @{ devices = 12 } }
    $act = Invoke-RestMethod "$base/api/v1/activate" -Method Post -ContentType 'application/json' -Body (@{ licenseKey = $lic.licenseKey.ToLower(); request = $req } | ConvertTo-Json -Depth 5)
    Assert ($act.activationToken) 'no activation token'
    $v = Api POST '/verify' @{ document = $act.license }
    Assert ($v.valid -and $v.payload.activation -eq 'online' -and $v.payload.leaseUntil -and $v.payload.licenseType -eq 'perpetual' -and -not $v.payload.entitlements.PSObject.Properties['nonit_assets']) "online file: $($v.payload | ConvertTo-Json -Compress)"
    $before = [datetimeoffset]$v.payload.supportUntil
    Api POST "/licenses/$($lic.id)/support" @{ years = 2 } | Out-Null
    $ci = Invoke-RestMethod "$base/api/v1/checkin" -Method Post -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($act.activationToken)" } `
        -Body (@{ installationId = $install; licenseId = $lic.id; productVersion = '1.1.1'; releaseDate = (Get-Date).ToUniversalTime().ToString('o'); usage = @{ devices = 15 } } | ConvertTo-Json)
    Assert ($ci.status -eq 'active') "check-in: $($ci.status)"
    $v2 = Api POST '/verify' @{ document = $ci.license }
    $after = [datetimeoffset]$v2.payload.supportUntil
    Assert ($v2.valid -and $after.Date -eq $before.AddYears(2).Date) "support $before -> $after"
    $d = Api GET "/licenses/$($lic.id)"
    Assert (@($d.activations).Count -eq 1 -and $d.activations[0].usage.devices -eq 15) 'usage not recorded'
    "$($lic.id): activated online, support $($before.ToString('yyyy-MM-dd')) -> $($after.ToString('yyyy-MM-dd')) via check-in; usage 15 recorded"
}

Check 'L06' 'A changed license file is rejected' {
    $doc = $script:subDoc | ConvertFrom-Json
    $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($doc.payload)) -replace '"devices":250', '"devices":25000'
    $doc.payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    $v = Api POST '/verify' @{ document = ($doc | ConvertTo-Json -Compress) }
    Assert (-not $v.valid -and $v.rejection -eq 'BadSignature') "tampered: $($v.rejection) $($v.message)"
    "rejected: $($v.message)"
}

Check 'L07' 'Database backup and stop' {
    $out = Zls @('backup', '-NonInteractive')
    Assert (@(Get-ChildItem (Join-Path $root 'data\backups') -Filter '*.dump').Count -eq 1) "no backup file: $out"
    Zls @('stop') | Out-Null
    Start-Sleep 2
    $up = try { (Invoke-WebRequest "$base/health" -UseBasicParsing -TimeoutSec 3).StatusCode } catch { 0 }
    Assert ($up -eq 0) 'server still answering after stop'
    $st = Zls @('status')
    Assert ($st -match 'PostgreSQL:\s+stopped') "status: $st"
    'backup written; server and PostgreSQL stopped'
}

$lines = @('# Local license server (Windows package)', '', '| ID | Test | Result | Detail |', '|---|---|---|---|')
$lines += $results | ForEach-Object { "| $($_.Id) | $($_.Name) | **$($_.Result)** | $(($_.Detail -replace '\|', '/' -replace "`r?`n", ' ').Trim()) |" }
$lines | Set-Content $Report -Encoding utf8
$lines | Write-Host
if (@($results | Where-Object Result -ne 'PASS').Count -gt 0) {
    Get-ChildItem (Join-Path $root 'data\logs') -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "---- $($_.Name)"; Get-Content $_.FullName -Tail 60 }
    try { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $zls stop | Out-Null } catch { }
    throw 'Local license server test failed.'
}
