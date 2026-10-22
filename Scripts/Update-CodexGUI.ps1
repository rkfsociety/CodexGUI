param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$TargetDirectory,
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$ExecutableName
)

$ErrorActionPreference = 'Stop'
$targetParent = Split-Path -Parent $TargetDirectory
$stagingDirectory = Join-Path $targetParent ('.CodexGUI-stage-' + [guid]::NewGuid().ToString('N'))
$backupDirectory = Join-Path $targetParent ('.CodexGUI-backup-' + [guid]::NewGuid().ToString('N'))
$oldVersionMoved = $false
$updateInstalled = $false

try {
    Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue
    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
        throw 'CodexGUI did not close within 60 seconds; the existing installation was left unchanged.'
    }

    New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $stagingDirectory -Force

    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $ExecutableName))) {
        throw 'В архиве отсутствует исполняемый файл CodexGUI.'
    }

    if (-not (Test-Path -LiteralPath $TargetDirectory -PathType Container)) {
        throw 'The installation directory no longer exists.'
    }

    Move-Item -LiteralPath $TargetDirectory -Destination $backupDirectory
    $oldVersionMoved = $true
    Move-Item -LiteralPath $stagingDirectory -Destination $TargetDirectory
    Start-Process -FilePath (Join-Path $TargetDirectory $ExecutableName) -WorkingDirectory $TargetDirectory
    $updateInstalled = $true

    try { Remove-Item -LiteralPath $backupDirectory -Recurse -Force -ErrorAction Stop }
    catch { Write-Warning "The update succeeded, but the previous version remains at $backupDirectory." }
    try { Remove-Item -LiteralPath (Split-Path -Parent $ArchivePath) -Recurse -Force -ErrorAction Stop }
    catch { Write-Warning 'The update succeeded, but temporary download files could not be removed.' }
}
catch {
    $failureMessage = $_.Exception.Message
    if ($oldVersionMoved -and -not $updateInstalled -and (Test-Path -LiteralPath $backupDirectory)) {
        try {
            if (Test-Path -LiteralPath $TargetDirectory) {
                Remove-Item -LiteralPath $TargetDirectory -Recurse -Force
            }
            Move-Item -LiteralPath $backupDirectory -Destination $TargetDirectory
        }
        catch {
            $failureMessage += "`nRollback also failed: $($_.Exception.Message) The previous version is preserved at $backupDirectory."
        }
    }

    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        "Не удалось завершить обновление CodexGUI. Старую версию можно запустить вручную.`n`n$failureMessage",
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
