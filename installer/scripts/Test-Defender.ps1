#Requires -Version 7.2
<#
.SYNOPSIS
  Scans release files with Microsoft Defender (current signatures) and fails when anything is detected.

.DESCRIPTION
  Each MSI is unpacked with an administrative install and each zip expanded, so every file the installers put on a
  server is scanned, not only the containers (a bundle or MSI can scan clean while a file inside it is detected when
  it is written to disk during installation). Writes <OutDir>\defender-scan.txt; exit code 0 = no threats.
#>
param(
    [Parameter(Mandatory)] [string[]] $Files,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$report = Join-Path $OutDir 'defender-scan.txt'
try { Update-MpSignature -ErrorAction Stop } catch { Write-Warning "Defender signatures not updated: $($_.Exception.Message)" }
$st = Get-MpComputerStatus
"Microsoft Defender $($st.AMProductVersion), signatures $($st.AntivirusSignatureVersion) ($($st.AntivirusSignatureLastUpdated))" | Set-Content $report
$mp = Join-Path (Get-ChildItem 'C:\ProgramData\Microsoft\Windows Defender\Platform' -Directory | Sort-Object Name | Select-Object -Last 1).FullName 'MpCmdRun.exe'
if (-not (Test-Path $mp)) { $mp = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe' }
$work = Join-Path $env:TEMP "zq-defender-$PID"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
$targets = @()
foreach ($f in $Files) {
    $item = Get-Item $f
    $targets += $item.FullName
    $dest = Join-Path $work $item.BaseName
    if ($item.Extension -eq '.msi') {
        $p = Start-Process msiexec.exe -ArgumentList '/a', "`"$($item.FullName)`"", '/qn', "TARGETDIR=`"$dest`"" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "Administrative install of $($item.Name) failed ($($p.ExitCode))." }
        $targets += $dest
    } elseif ($item.Extension -eq '.zip') {
        Expand-Archive $item.FullName -DestinationPath $dest -Force
        $targets += $dest
    }
}
$found = 0
foreach ($t in $targets) {
    $out = & $mp -Scan -ScanType 3 -File $t -DisableRemediation 2>&1 | Out-String
    $code = $LASTEXITCODE
    $threats = [regex]::Matches($out, 'Threat\s+:\s+(.+)') | ForEach-Object { $_.Groups[1].Value.Trim() }
    $paths = [regex]::Matches($out, '(?m)^\s+file\s+:\s+(.+)$') | ForEach-Object { $_.Groups[1].Value.Trim().Replace($work, '<unpacked>') }
    $name = $t.Replace($work, '<unpacked>')
    if ($code -eq 2 -or $threats) {
        $found++
        "DETECTED $name : $($threats -join ', ') in $($paths -join '; ')" | Add-Content $report
    } else { "clean    $name" | Add-Content $report }
}
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Get-Content $report
if ($found) { Write-Error "Microsoft Defender detected $found item(s); see $report."; exit 1 }
exit 0
