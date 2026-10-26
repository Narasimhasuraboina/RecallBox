$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'RecallBox.App\RecallBox.App.csproj'
$dotnetExe = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'

try {
    $status = Invoke-RestMethod 'http://localhost:5180/api/status' -TimeoutSec 2
    if ($status.localUrl -eq 'http://localhost:5180') { exit 0 }
} catch { }

if (-not (Test-Path $projectFile)) { throw "RecallBox project not found at $projectFile" }
if (-not (Test-Path $dotnetExe)) { throw ".NET SDK not found. Install the .NET 8 SDK first." }

Start-Process -FilePath $dotnetExe `
    -ArgumentList @('run', '--configuration', 'Release', '--project', $projectFile) `
    -WorkingDirectory $projectRoot `
    -WindowStyle Hidden | Out-Null
