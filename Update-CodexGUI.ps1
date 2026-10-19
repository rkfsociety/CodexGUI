param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$TargetDirectory,
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$ExecutableName
)

$ErrorActionPreference = 'Stop'
$stagingDirectory = Join-Path $env:TEMP ('CodexGUI-stage-' + [guid]::NewGuid().ToString('N'))

try {
    Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $stagingDirectory -Force

    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $ExecutableName))) {
        throw 'В архиве отсутствует исполняемый файл CodexGUI.'
    }

    Get-ChildItem -LiteralPath $stagingDirectory -Force | Copy-Item -Destination $TargetDirectory -Recurse -Force
    Start-Process -FilePath (Join-Path $TargetDirectory $ExecutableName) -WorkingDirectory $TargetDirectory
    Remove-Item -LiteralPath (Split-Path -Parent $ArchivePath) -Recurse -Force
}
catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        "Не удалось завершить обновление CodexGUI. Старую версию можно запустить вручную.`n`n$($_.Exception.Message)",
        'Обновление CodexGUI',
        [System.Windows.MessageBoxButton]::OK,
        [System.Windows.MessageBoxImage]::Error
    ) | Out-Null
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
