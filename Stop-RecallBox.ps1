$ErrorActionPreference = 'Stop'
$listeners = Get-NetTCPConnection -LocalPort 5180 -State Listen -ErrorAction SilentlyContinue
$appIds = $listeners | Select-Object -ExpandProperty OwningProcess -Unique
foreach ($processId in $appIds) {
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($process -and $process.ProcessName -eq 'RecallBox.App') {
        Stop-Process -Id $processId -Force
    }
}
