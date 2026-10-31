<#
.SYNOPSIS
  Zentriq License Server - local edition for Windows (testing before going public).

.DESCRIPTION
  Runs the license server and its own PostgreSQL from this folder; nothing is installed and nothing listens beyond
  this computer (PostgreSQL on 127.0.0.1:5439, the server on http://localhost:5080).

    .\zls.cmd setup      database, production signing key (key ceremony), owner account of the portal
    .\zls.cmd start      start PostgreSQL and the server, open the portal  (-Background: return to the prompt)
    .\zls.cmd stop       stop the server and PostgreSQL
    .\zls.cmd status     what is running, key id and fingerprint
    .\zls.cmd backup     database backup into data\backups (the signing key is backed up separately, offline)
    .\zls.cmd key-info   key id, fingerprint and public key

  Works with Windows PowerShell 5.1 and PowerShell 7.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][ValidateSet('setup', 'start', 'stop', 'status', 'backup', 'key-info')][string] $Command = 'status',
    [string] $KeyId,
    [string] $OwnerEmail,
    [int] $HttpPort = 5080,
    [int] $PgPort = 5439,
    [switch] $Background,
    [switch] $NoBrowser,
    # Ask for the signing key passphrase at every start instead of keeping it protected for this Windows user (DPAPI).
    [switch] $NoStorePassphrase,
    # No prompts: passphrases come from ZLS_SIGNING_PASSPHRASE and ZLS_ADMIN_PASSPHRASE (automated tests).
    [switch] $NonInteractive
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root    = $PSScriptRoot
$Server  = Join-Path $Root 'server\Zentriq.LicenseServer.exe'
$PgBin   = Join-Path $Root 'pgsql\bin'
$Data    = Join-Path $Root 'data'
$PgData  = Join-Path $Data 'pg'
$Keys    = Join-Path $Data 'keys'
$Vault   = Join-Path $Data 'protected'
$Logs    = Join-Path $Data 'logs'
$Config  = Join-Path $Data 'config.json'
$PidFile = Join-Path $Data 'server.pid'
$DbName  = 'zentriq_license'
$DbUser  = 'zls'

function Say([string] $Text, [string] $Color = 'Gray') { Write-Host $Text -ForegroundColor $Color }
function Fail([string] $Text) { Write-Host $Text -ForegroundColor Red; exit 1 }

function Plain([Security.SecureString] $s) {
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}
# DPAPI (current Windows user): the value can be read back only by this user on this computer.
# Windows DPAPI through .NET directly (no dependency on the PowerShell security module).
try { Add-Type -AssemblyName System.Security -ErrorAction Stop } catch { }
$Entropy = [Text.Encoding]::UTF8.GetBytes('Zentriq License Server local edition')
function Protect-Value([string] $Name, [string] $Value) {
    New-Item -ItemType Directory -Force -Path $Vault | Out-Null
    $enc = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($Value), $Entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    Set-Content (Join-Path $Vault "$Name.dpapi") ([Convert]::ToBase64String($enc)) -Encoding ascii
}
function Unprotect-Value([string] $Name) {
    $f = Join-Path $Vault "$Name.dpapi"
    if (-not (Test-Path $f)) { return $null }
    $dec = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String((Get-Content $f -Raw).Trim()), $Entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [Text.Encoding]::UTF8.GetString($dec)
}
function New-RandomSecret {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return ([Convert]::ToBase64String($bytes) -replace '[+/=]', 'x')
}
function Read-Phrase([string] $Prompt, [int] $Min, [switch] $Confirm) {
    while ($true) {
        $a = Plain (Read-Host -Prompt $Prompt -AsSecureString)
        if ($a.Length -lt $Min) { Say "  Use at least $Min characters." Yellow; continue }
        if ($Confirm) {
            $b = Plain (Read-Host -Prompt '  Repeat it' -AsSecureString)
            if ($a -ne $b) { Say '  The two entries differ; try again.' Yellow; continue }
        }
        return $a
    }
}

function Get-Config { if (Test-Path $Config) { return Get-Content $Config -Raw | ConvertFrom-Json } return $null }
function Get-ConnectionString {
    $pw = Unprotect-Value 'database'
    if (-not $pw) { Fail 'The database is not set up. Run: .\zls.cmd setup' }
    return "Host=127.0.0.1;Port=$PgPort;Database=$DbName;Username=$DbUser;Password=$pw"
}

function Invoke-Server([string[]] $Arguments, [hashtable] $Env) {
    $saved = @{}
    foreach ($k in $Env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $Env[$k]) }
    Push-Location (Split-Path $Server)
    try { & $Server @Arguments | Out-Host; return $LASTEXITCODE }
    finally { Pop-Location; foreach ($k in $Env.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
}

function Test-PgRunning { & (Join-Path $PgBin 'pg_ctl.exe') status -D $PgData *> $null; return ($LASTEXITCODE -eq 0) }
function Start-Pg {
    if (Test-PgRunning) { return }
    New-Item -ItemType Directory -Force -Path $Logs | Out-Null
    # Started detached with its output in files: the database server it launches must not inherit this console's
    # handles (a caller reading this script's output would otherwise wait for as long as PostgreSQL runs).
    $p = Start-Process -FilePath (Join-Path $PgBin 'pg_ctl.exe') -WindowStyle Hidden -PassThru `
        -ArgumentList @('start', '-D', "`"$PgData`"", '-l', "`"$(Join-Path $Logs 'postgresql.log')`"", '-w', '-t', '60', '-o', "`"-p $PgPort -c listen_addresses=127.0.0.1`"") `
        -RedirectStandardOutput (Join-Path $Logs 'pg_ctl.log') -RedirectStandardError (Join-Path $Logs 'pg_ctl-error.log')
    if (-not $p.WaitForExit(90000)) { Fail 'PostgreSQL did not start within 90 seconds.' }
    if (-not (Test-PgRunning)) { Fail "PostgreSQL did not start; see $(Join-Path $Logs 'postgresql.log') and pg_ctl-error.log" }
}
function Stop-Pg { if (Test-PgRunning) { & (Join-Path $PgBin 'pg_ctl.exe') stop -D $PgData -m fast -w | Out-Null } }

function Get-ServerEnv([string] $Passphrase) {
    $cfg = Get-Config
    if (-not $cfg) { Fail 'Not set up yet. Run: .\zls.cmd setup' }
    return @{
        'ConnectionStrings__LicenseServer'      = Get-ConnectionString
        'LicenseServer__SigningKeyFile'         = $cfg.keyFile
        'LicenseServer__SigningKeyId'           = $cfg.keyId
        'LicenseServer__PublicUrl'              = "http://localhost:$HttpPort"
        'ZLS_SIGNING_PASSPHRASE'                = $Passphrase
        'ASPNETCORE_ENVIRONMENT'                = 'Production'
    }
}
function Get-SigningPhrase {
    if ($env:ZLS_SIGNING_PASSPHRASE) { return $env:ZLS_SIGNING_PASSPHRASE }
    $p = Unprotect-Value 'signing'
    if ($p) { return $p }
    if ($NonInteractive) { Fail 'Set ZLS_SIGNING_PASSPHRASE.' }
    return Plain (Read-Host -Prompt 'Signing key passphrase' -AsSecureString)
}

function Invoke-Setup {
    if (-not (Test-Path $Server)) { Fail "Missing $Server - unpack the whole package." }
    New-Item -ItemType Directory -Force -Path $Data, $Logs, $Keys | Out-Null
    $cfg = Get-Config

    # 1. Database (own PostgreSQL cluster in data\pg, loopback only, SCRAM password kept with DPAPI).
    if (-not (Test-Path (Join-Path $PgData 'PG_VERSION'))) {
        Say '1/4  Creating the database cluster (data\pg)...' Cyan
        $dbPw = New-RandomSecret
        $pwFile = Join-Path $Data 'initdb.tmp'
        Set-Content $pwFile $dbPw -Encoding ascii -NoNewline
        try {
            & (Join-Path $PgBin 'initdb.exe') -D $PgData -U $DbUser -A scram-sha-256 --pwfile=$pwFile -E UTF8 --no-locale *> (Join-Path $Logs 'initdb.log')
            if ($LASTEXITCODE -ne 0) { Fail "initdb failed; see $(Join-Path $Logs 'initdb.log')" }
        } finally { Remove-Item $pwFile -Force -ErrorAction SilentlyContinue }
        Protect-Value 'database' $dbPw
        Start-Pg
        $env:PGPASSWORD = $dbPw
        try { & (Join-Path $PgBin 'createdb.exe') -w -h 127.0.0.1 -p $PgPort -U $DbUser $DbName; if ($LASTEXITCODE -ne 0) { Fail 'createdb failed' } }
        finally { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }
    } else { Say '1/4  Database cluster exists.' Cyan; Start-Pg }
    $cs = Get-ConnectionString
    if ((Invoke-Server @('migrate') @{ 'ConnectionStrings__LicenseServer' = $cs }) -ne 0) { Fail 'Database migration failed.' }

    # 2. Production signing key: generated here, encrypted, never leaves this computer.
    if ($cfg -and $cfg.keyFile -and (Test-Path $cfg.keyFile)) {
        Say "2/4  Signing key $($cfg.keyId) exists (a key is never replaced by setup)." Cyan
    } else {
        if (-not $KeyId) { $KeyId = 'zentriq-prod-' + (Get-Date).ToUniversalTime().ToString('yyyy-MM') }
        Say "2/4  Key ceremony: creating signing key $KeyId" Cyan
        Say '     Choose a long passphrase (16+ characters, e.g. five random words). Write it down and keep it apart from the key backups.'
        $phrase = if ($env:ZLS_SIGNING_PASSPHRASE) { $env:ZLS_SIGNING_PASSPHRASE } elseif ($NonInteractive) { Fail 'Set ZLS_SIGNING_PASSPHRASE.' } else { Read-Phrase '     Signing key passphrase' 16 -Confirm }
        if ((Invoke-Server @('keygen', '--out', $Keys, '--key-id', $KeyId) @{ 'ZLS_SIGNING_PASSPHRASE' = $phrase }) -ne 0) { Fail 'Key generation failed.' }
        if (-not $NoStorePassphrase) { Protect-Value 'signing' $phrase }
        $cfg = [pscustomobject]@{ keyId = $KeyId; keyFile = (Join-Path $Keys "$KeyId.private.pem"); createdAt = (Get-Date).ToUniversalTime().ToString('o'); httpPort = $HttpPort; pgPort = $PgPort }
        $cfg | ConvertTo-Json | Set-Content $Config -Encoding utf8
    }

    # 3. Owner account of the portal.
    Say '3/4  Owner account of the license portal' Cyan
    if (-not $OwnerEmail) { if ($NonInteractive) { Fail 'Pass -OwnerEmail.' } $OwnerEmail = Read-Host '     Your email (portal sign-in)' }
    $adminPhrase = if ($env:ZLS_ADMIN_PASSPHRASE) { $env:ZLS_ADMIN_PASSPHRASE } elseif ($NonInteractive) { Fail 'Set ZLS_ADMIN_PASSPHRASE.' } else { Read-Phrase '     Portal passphrase (12+ characters)' 12 -Confirm }
    $code = Invoke-Server @('create-admin', '--email', $OwnerEmail, '--name', $OwnerEmail) @{ 'ConnectionStrings__LicenseServer' = $cs; 'ZLS_ADMIN_PASSPHRASE' = $adminPhrase }
    if ($code -ne 0) { Say '     (Account not created - it may exist already.)' Yellow }

    # 4. Summary.
    Say '4/4  Checking the key...' Cyan
    Invoke-KeyInfo
    Say ''
    Say 'Done. Next:' Green
    Say "  .\zls.cmd start            portal: http://localhost:$HttpPort/admin   offline activation page: http://localhost:$HttpPort/"
    Say "  Back up data\keys\$($cfg.keyId).private.pem to two offline places NOW (see data\keys\$($cfg.keyId).ceremony.txt)."
    Say "  Send Zentriq only data\keys\$($cfg.keyId).product-entry.json and confirm the fingerprint by phone."
}

function Invoke-KeyInfo {
    $envs = Get-ServerEnv (Get-SigningPhrase)
    $null = Invoke-Server @('key-info') $envs
}

function Invoke-Start {
    Start-Pg
    $envs = Get-ServerEnv (Get-SigningPhrase)
    $url = "http://localhost:$HttpPort"
    if ($Background) {
        if ((Test-Path $PidFile) -and (Get-Process -Id ([int](Get-Content $PidFile)) -ErrorAction SilentlyContinue)) { Say "Already running: $url/admin" Green; return }
        foreach ($k in $envs.Keys) { [Environment]::SetEnvironmentVariable($k, $envs[$k]) }
        $p = Start-Process -FilePath $Server -ArgumentList @('serve', '--urls', $url) -WorkingDirectory (Split-Path $Server) -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $Logs 'server.log') -RedirectStandardError (Join-Path $Logs 'server-error.log')
        foreach ($k in $envs.Keys) { [Environment]::SetEnvironmentVariable($k, $null) }
        Set-Content $PidFile $p.Id
        $deadline = (Get-Date).AddSeconds(60)
        do {
            Start-Sleep -Milliseconds 500
            try { $ok = (Invoke-WebRequest "$url/health" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { $ok = $false }
            if ($p.HasExited) { Fail "The server stopped; see $(Join-Path $Logs 'server-error.log')" }
        } while (-not $ok -and (Get-Date) -lt $deadline)
        if (-not $ok) { Fail "The server did not answer; see $(Join-Path $Logs 'server.log')" }
        Say "Running: $url/admin  (stop with .\zls.cmd stop)" Green
        if (-not $NoBrowser) { Start-Process "$url/admin" }
    } else {
        Say "Starting on $url - portal $url/admin. Press Ctrl+C to stop the server (PostgreSQL keeps running; .\zls.cmd stop ends it)." Green
        if (-not $NoBrowser) { Start-Job -ScriptBlock { param($u) Start-Sleep 4; Start-Process "$u/admin" } -ArgumentList $url | Out-Null }
        $null = Invoke-Server @('serve', '--urls', $url) $envs
    }
}

function Invoke-Stop {
    if (Test-Path $PidFile) {
        $p = Get-Process -Id ([int](Get-Content $PidFile)) -ErrorAction SilentlyContinue
        if ($p) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(10000) | Out-Null }
        Remove-Item $PidFile -Force
    }
    Stop-Pg
    Say 'Stopped.' Green
}

function Invoke-Status {
    $cfg = Get-Config
    if (-not $cfg) { Say 'Not set up. Run: .\zls.cmd setup' Yellow; return }
    $srv = (Test-Path $PidFile) -and (Get-Process -Id ([int](Get-Content $PidFile)) -ErrorAction SilentlyContinue)
    Say "PostgreSQL:  $(if (Test-PgRunning) { "running on 127.0.0.1:$PgPort" } else { 'stopped' })"
    Say "Server:      $(if ($srv) { "running (background) on http://localhost:$HttpPort" } else { 'not running in the background' })"
    Say "Signing key: $($cfg.keyId)  ($($cfg.keyFile))"
    $rec = Join-Path $Keys "$($cfg.keyId).ceremony.txt"
    if (Test-Path $rec) { Say ((Get-Content $rec | Select-String 'Fingerprint').ToString().Trim()) }
}

function Invoke-Backup {
    Start-Pg
    $dir = Join-Path $Data 'backups'; New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir ("$DbName-" + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.dump')
    $env:PGPASSWORD = Unprotect-Value 'database'
    if (-not $env:PGPASSWORD) { Fail 'The database is not set up. Run: .\zls.cmd setup' }
    try { & (Join-Path $PgBin 'pg_dump.exe') -w -h 127.0.0.1 -p $PgPort -U $DbUser -Fc -f $file $DbName; if ($LASTEXITCODE -ne 0) { Fail 'pg_dump failed' } }
    finally { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }
    Say "Database backup: $file" Green
    Say 'The signing key is not in this backup: keep data\keys\*.private.pem backed up offline, its passphrase separately.'
}

switch ($Command) {
    'setup'    { Invoke-Setup }
    'start'    { Invoke-Start }
    'stop'     { Invoke-Stop }
    'status'   { Invoke-Status }
    'backup'   { Invoke-Backup }
    'key-info' { Invoke-KeyInfo }
}
