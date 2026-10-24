<#
.SYNOPSIS
Captures the current CodexGUI interface from the checked-out XAML without launching CodexGUI.

.DESCRIPTION
Runs the existing WPF preview renderer in a dedicated hidden STA PowerShell process. It renders the current source
layout; it does not start CodexGUI, App Server, or read the user's chats or application state. Choose Reference to
render the supplied design fixture with the same current source controls.

.EXAMPLE
pwsh -NoProfile -File Scripts/Capture-CurrentInterface.ps1

.EXAMPLE
pwsh -NoProfile -File Scripts/Capture-CurrentInterface.ps1 -View Reference -VerifyLayout

.EXAMPLE
pwsh -NoProfile -File Scripts/Capture-CurrentInterface.ps1 -OutputPath .\preview\current-interface.png -Width 1585 -Height 992
#>
[CmdletBinding()]
param(
    [ValidateSet('Workspace', 'Reference')]
    [string]$View = 'Reference',
    [string]$OutputPath = (Join-Path $env:TEMP 'CodexGUI-current.png'),
    [ValidateSet('Empty', 'Draft', 'Attachments', 'Queued', 'Running')]
    [string]$ComposerState = 'Empty',
    [int]$Width = 1585,
    [int]$Height = 992,
    [switch]$VerifyLayout
)

$ErrorActionPreference = 'Stop'
$previewScript = Join-Path $PSScriptRoot 'Capture-WorkspacePreview.ps1'
if (-not (Test-Path -LiteralPath $previewScript)) {
    throw "Workspace preview renderer was not found: $previewScript"
}

$resolvedOutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$pwshPath = (Get-Process -Id $PID).Path
$startInfo = [Diagnostics.ProcessStartInfo]::new($pwshPath)
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
foreach ($argument in @(
    '-NoProfile', '-STA', '-File', $previewScript,
    '-View', $View,
    '-OutputPath', $resolvedOutputPath,
    '-ComposerState', $ComposerState,
    '-Width', $Width.ToString([Globalization.CultureInfo]::InvariantCulture),
    '-Height', $Height.ToString([Globalization.CultureInfo]::InvariantCulture)
)) {
    [void]$startInfo.ArgumentList.Add($argument)
}
if ($VerifyLayout -or $View -eq 'Reference') { [void]$startInfo.ArgumentList.Add('-VerifyLayout') }

$process = [Diagnostics.Process]::Start($startInfo)
if ($null -eq $process) { throw 'Could not start the STA PowerShell preview renderer.' }
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()
$process.WaitForExit()
$stdout = $stdoutTask.GetAwaiter().GetResult()
$stderr = $stderrTask.GetAwaiter().GetResult()
if ($process.ExitCode -ne 0) {
    throw "Workspace preview renderer failed with exit code $($process.ExitCode).`n$stderr"
}
if (-not [string]::IsNullOrWhiteSpace($stderr)) { Write-Warning $stderr.Trim() }
if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) {
    throw "Preview renderer exited successfully but did not create $resolvedOutputPath."
}

[pscustomobject]@{
    Path = $resolvedOutputPath
    View = $View
    Width = $Width
    Height = $Height
    ComposerState = $ComposerState
    CodexGUIStarted = $false
}
