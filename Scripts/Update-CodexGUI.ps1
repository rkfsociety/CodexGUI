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
    # Only clean up the dedicated download folder created by AppUpdater. Never
    # recursively delete an arbitrary parent directory supplied via ArchivePath.
    try {
        $downloadDirectory = [System.IO.DirectoryInfo]::new((Split-Path -Parent $ArchivePath))
        $tempDirectory = [System.IO.DirectoryInfo]::new([System.IO.Path]::GetTempPath())
        $downloadParent = $downloadDirectory.Parent.FullName.TrimEnd([char]92)
        $tempParent = $tempDirectory.FullName.TrimEnd([char]92)
        if ($downloadDirectory.Name -notmatch '^CodexGUI-update-[0-9a-f]{32}
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
 -or
            -not [string]::Equals($downloadParent, $tempParent, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $ArchivePath) -ne 'CodexGUI-win-x64.zip' -or
            ($downloadDirectory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Unexpected update download directory; leaving its contents unchanged.'
        }

        # Remove only our own files. An unexpected file must keep the directory
        # non-empty instead of being removed with unrelated user data.
        foreach ($fileName in @('CodexGUI-win-x64.zip', 'CodexGUI-win-x64.zip.sha256',
                                'CodexGUI-win-x64.zip.sig', 'Update-CodexGUI.ps1')) {
            [System.IO.File]::Delete((Join-Path $downloadDirectory.FullName $fileName))
        }
        [System.IO.Directory]::Delete($downloadDirectory.FullName)
    }
    catch { Write-Warning "The update succeeded, but temporary download files could not be fully removed: $($_.Exception.Message)" }
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
