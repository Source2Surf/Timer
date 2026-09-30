$ErrorActionPreference = 'Stop'

$projectPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Backend/Timer.Backend/Timer.Backend.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Timer.Backend project not found at $projectPath"
}

& dotnet run --no-build --no-restore --configuration Release --project $projectPath -- migrate
exit $LASTEXITCODE
