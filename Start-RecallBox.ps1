param([switch]$Lan)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'RecallBox.App\RecallBox.App.csproj'
$dotnetExe = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'

if (-not (Test-Path $projectFile)) { throw "RecallBox project not found at $projectFile" }
if (-not (Test-Path $dotnetExe)) { throw ".NET SDK not found. Install the .NET 8 SDK first." }

Set-Location $projectRoot
$arguments = @('run', '--configuration', 'Release', '--project', $projectFile)
if ($Lan) { $arguments += '--'; $arguments += '--lan' }
& $dotnetExe @arguments
exit $LASTEXITCODE
