[CmdletBinding()]
param(
    [switch]$DisposableDatabases,
    [switch]$NoRestore,
    [switch]$BackendOnly,
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (!$DisposableDatabases) {
    throw 'This suite replaces both test database schemas. Pass -DisposableDatabases only for disposable test databases.'
}

foreach ($variable in @('TIMER_TEST_MYSQL', 'TIMER_TEST_POSTGRES')) {
    $connection = [Environment]::GetEnvironmentVariable($variable)
    if ([string]::IsNullOrWhiteSpace($connection)) {
        throw "Set $variable to a disposable loopback database before running acceptance."
    }
    $parsed = [System.Data.Common.DbConnectionStringBuilder]::new()
    try { $parsed.set_ConnectionString($connection) }
    catch { throw "$variable must be a valid connection string." }
    $hostKey = if ($variable -eq 'TIMER_TEST_MYSQL') { 'Server' } else { 'Host' }
    if (!$parsed.ContainsKey($hostKey) -or !$parsed.ContainsKey('Database')) {
        throw "$variable requires $hostKey and Database fields."
    }
    if (@('127.0.0.1', 'localhost', '::1') -notcontains [string]$parsed[$hostKey] -or
        [string]$parsed['Database'] -notmatch 'test') {
        throw "$variable must target loopback and a database whose name contains 'test'."
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (!$BackendOnly -and !(Test-Path -LiteralPath (Join-Path $repoRoot 'Plugin/Timer.Tests/Timer.Tests.csproj'))) {
    throw 'This source bundle omits the game plugin tests. Run acceptance with -BackendOnly.'
}
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $runName = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N')
    $ResultsDirectory = Join-Path $repoRoot "artifacts/backend-acceptance/$runName"
}
$ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$phaseResults = [System.Collections.Generic.List[object]]::new()

function Invoke-AcceptancePhase {
    param([string]$Name, [string]$Project, [string]$Filter)
    $arguments = @(
        'test', (Join-Path $repoRoot $Project), '--configuration', 'Release',
        '-p:CIBuild=true', '-p:CheckForOverflowUnderflow=true', '-p:TreatWarningsAsErrors=true',
        '--logger', 'console;verbosity=minimal',
        '--logger', "trx;LogFileName=$Name.trx", '--results-directory', $ResultsDirectory
    )
    if ($NoRestore) { $arguments += '--no-restore' }
    if ($Filter) { $arguments += @('--filter', $Filter) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Acceptance phase '$Name' failed." }

    [xml]$report = Get-Content -Raw -LiteralPath (Join-Path $ResultsDirectory "$Name.trx")
    $tests = @($report.SelectNodes("//*[local-name()='UnitTestResult']"))
    $notPassed = @($tests | Where-Object { $_.outcome -ne 'Passed' })
    if ($tests.Count -eq 0 -or $notPassed.Count -ne 0) {
        throw "Acceptance phase '$Name' must execute every selected test; skipped tests are a failure."
    }
    $phaseResults.Add([pscustomobject]@{ Phase = $Name; Passed = $tests.Count })
}

$flagNames = @('TIMER_TEST_MASTER_MIGRATION', 'TIMER_TEST_POSTGRES_MASTER_MIGRATION', 'TIMER_TEST_TLS')
$savedFlags = @{}
foreach ($flag in $flagNames) {
    $savedFlags[$flag] = [Environment]::GetEnvironmentVariable($flag)
    [Environment]::SetEnvironmentVariable($flag, '1', 'Process')
}

try {
    $buildArguments = @('build', (Join-Path $repoRoot 'Backend/Timer.Backend/Timer.Backend.csproj'), '--configuration', 'Release',
        '-p:CIBuild=true', '-p:CheckForOverflowUnderflow=true', '-p:TreatWarningsAsErrors=true')
    if ($NoRestore) { $buildArguments += '--no-restore' }
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'The backend Release build failed.' }

    # Migration acceptance recreates the published master schema and upgrades it through
    # the real CLI. Running it first also provisions the schema needed by concurrency tests.
    Invoke-AcceptancePhase 'migration' 'Backend/Timer.Backend.Storage.Tests/Timer.Backend.Storage.Tests.csproj' 'FullyQualifiedName~MasterSqlMigrationAcceptanceTests'
    Invoke-AcceptancePhase 'storage' 'Backend/Timer.Backend.Storage.Tests/Timer.Backend.Storage.Tests.csproj' 'FullyQualifiedName!~MasterSqlMigrationAcceptanceTests'
    Invoke-AcceptancePhase 'backend' 'Backend/Timer.Backend.Tests/Timer.Backend.Tests.csproj' ''
    if (!$BackendOnly) {
        Invoke-AcceptancePhase 'plugin' 'Plugin/Timer.Tests/Timer.Tests.csproj' ''
    }

    $phaseResults | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.json')
    $phaseResults | Format-Table -AutoSize
    Write-Host "Acceptance passed with no skipped tests. Reports: $ResultsDirectory"
}
finally {
    foreach ($flag in $flagNames) {
        [Environment]::SetEnvironmentVariable($flag, $savedFlags[$flag], 'Process')
    }
}
