[CmdletBinding()]
param(
    [switch] $BackupConfirmed
)

$ErrorActionPreference = 'Stop'

if (-not $BackupConfirmed)
{
    throw 'Stop every writer and verify a restorable database backup first. Re-run with -BackupConfirmed only after that check.'
}

$projectPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Backend/Timer.Backend/Timer.Backend.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Timer.Backend project not found at $projectPath"
}

# This wrapper intentionally has no database-credential parameters. Supply the
# temporary migration account through an environment-specific configuration source.
& dotnet run --no-build --no-restore --configuration Release --project $projectPath -- convert-run-dates --backup-confirmed
exit $LASTEXITCODE
