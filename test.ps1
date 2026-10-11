#Requires -Version 7.2
<#
.SYNOPSIS
  Runs the Zentriq test suites and writes TRX results to artifacts/test-results.

.DESCRIPTION
  * Unit tests (always).
  * Database integration tests (migrations applied to a throw-away database, least-privilege role
    checks, trial and job-queue behaviour) when -PostgresConnection or ZENTRIQ_TEST_PG is set to a
    connection string for a role that can create databases and roles, e.g.
      "Host=localhost;Port=5432;Username=postgres;Password=..."
    Without it those tests are reported as skipped, not passed.

.EXAMPLE
  ./test.ps1 -PostgresConnection "Host=localhost;Username=postgres;Password=secret"
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $PostgresConnection = $env:ZENTRIQ_TEST_PG,
    [switch] $NoBuild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
. (Join-Path $root 'deployment/scripts/common.ps1')
trap { Write-Failure $_; break }

$results = Join-Path $root 'artifacts/test-results'
New-Item -ItemType Directory -Force -Path $results | Out-Null
if ($PostgresConnection) { $env:ZENTRIQ_TEST_PG = $PostgresConnection; Write-Host 'Database integration tests: ENABLED' }
else { Write-Warning 'ZENTRIQ_TEST_PG not set: database integration tests will be SKIPPED.' }

Write-Section 'dotnet test'
$testArgs = @('test', (Join-Path $root 'backend/Zentriq.Tests/Zentriq.Tests.csproj'), '--configuration', $Configuration,
          '--logger', 'trx;LogFileName=zentriq-tests.trx', '--logger', 'console;verbosity=normal', '--results-directory', $results)
if ($NoBuild) { $testArgs += '--no-build' }
& dotnet @testArgs
$code = $LASTEXITCODE

# Summarise the TRX so the outcome is visible in CI logs and in the test report.
$trx = Get-ChildItem $results -Filter '*.trx' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($trx) {
    [xml]$x = Get-Content $trx.FullName
    $c = $x.TestRun.ResultSummary.Counters
    $summary = "total=$($c.total) passed=$($c.passed) failed=$($c.failed) notExecuted=$($c.notExecuted) (skipped)"
    Write-Host "Test summary: $summary"
    Set-Content -Path (Join-Path $results 'summary.txt') -Value $summary
}
if ($code -ne 0) { throw "Tests failed (exit code $code)." }
Write-Host 'Tests passed.' -ForegroundColor Green
