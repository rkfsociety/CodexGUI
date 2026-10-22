<#
.SYNOPSIS
Renders the current workspace XAML to PNG without launching CodexGUI.

.DESCRIPTION
Loads App.xaml resources and Views/WorkspaceView.xaml in a standalone STA PowerShell process,
then adds local preview messages so the chat and composer can be reviewed together.

.EXAMPLE
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1

.EXAMPLE
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -OutputPath .\preview\workspace.png -Width 1600 -Height 1000
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $env:TEMP 'CodexGUI-workspace-preview.png'),
    [ValidateRange(960, 2560)]
    [int]$Width = 1440,
    [ValidateRange(720, 1800)]
    [int]$Height = 1000
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne [Threading.ApartmentState]::STA) {
    throw 'Run this preview with: pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1'
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$projectRoot = Split-Path -Parent $PSScriptRoot
$appXamlPath = Join-Path $projectRoot 'App.xaml'
$workspaceXamlPath = Join-Path $projectRoot 'Views\WorkspaceView.xaml'
if (-not (Test-Path -LiteralPath $appXamlPath) -or -not (Test-Path -LiteralPath $workspaceXamlPath)) {
    throw 'App.xaml or Views/WorkspaceView.xaml was not found beside this script.'
}

$application = [System.Windows.Application]::Current
if ($null -eq $application) {
    $application = [System.Windows.Application]::new()
}
$application.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown

$appDocument = [xml]::new()
$appDocument.Load($appXamlPath)
$resourceNode = $appDocument.SelectSingleNode("/*[local-name()='Application']/*[local-name()='Application.Resources']")
if ($null -eq $resourceNode) {
    throw 'App.xaml does not contain Application.Resources.'
}
$resourcesXaml = @"
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
$($resourceNode.InnerXml)
</ResourceDictionary>
"@
$application.Resources.MergedDictionaries.Add([System.Windows.Markup.XamlReader]::Parse($resourcesXaml))

$workspaceXaml = [System.IO.File]::ReadAllText($workspaceXamlPath)
$workspace = [System.Windows.Markup.XamlReader]::Parse($workspaceXaml)
if ($workspace -isnot [System.Windows.Controls.Grid]) {
    throw 'WorkspaceView.xaml must have a Grid root element.'
}
$conversation = $workspace.FindName('Conversation')
if ($conversation -isnot [System.Windows.Controls.StackPanel]) {
    throw 'The Conversation panel could not be found in WorkspaceView.xaml.'
}

function New-PreviewMessage([bool]$IsUser, [string]$Text) {
    $row = [System.Windows.Controls.StackPanel]::new()
    $row.HorizontalAlignment = if ($IsUser) { [System.Windows.HorizontalAlignment]::Right } else { [System.Windows.HorizontalAlignment]::Left }
    $row.MaxWidth = 880
    $row.Margin = [System.Windows.Thickness]::new(0, 12, 0, 14)

    if (-not $IsUser) {
        $label = [System.Windows.Controls.TextBlock]::new()
        $label.Text = '✳   CODEX'
        $label.FontSize = 12
        $label.FontWeight = [System.Windows.FontWeights]::SemiBold
        $label.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#92E5C7')
        $label.Margin = [System.Windows.Thickness]::new(3, 0, 0, 9)
        [void]$row.Children.Add($label)
    }

    $body = [System.Windows.Controls.TextBlock]::new()
    $body.Text = $Text
    $body.FontSize = 16
    $body.LineHeight = 27
    $body.TextWrapping = [System.Windows.TextWrapping]::Wrap
    $body.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString($(if ($IsUser) { '#EAF4F0' } else { '#ECF2F5' }))

    $bubble = [System.Windows.Controls.Border]::new()
    $bubble.Background = [System.Windows.Media.BrushConverter]::new().ConvertFromString($(if ($IsUser) { '#1C3A32' } else { '#16212B' }))
    $bubble.BorderBrush = [System.Windows.Media.BrushConverter]::new().ConvertFromString($(if ($IsUser) { '#3D6B5A' } else { '#2A3946' }))
    $bubble.BorderThickness = [System.Windows.Thickness]::new(1)
    $bubble.CornerRadius = [System.Windows.CornerRadius]::new(17)
    $bubble.Padding = if ($IsUser) { [System.Windows.Thickness]::new(18, 14, 18, 14) } else { [System.Windows.Thickness]::new(20, 18, 20, 18) }
    $bubble.Child = $body
    [void]$row.Children.Add($bubble)
    return $row
}

$conversation.Children.Clear()
[void]$conversation.Children.Add((New-PreviewMessage -IsUser $true -Text 'Проверь экран чата и предложи, как сделать его удобнее для долгой работы.'))
[void]$conversation.Children.Add((New-PreviewMessage -IsUser $false -Text 'Я посмотрю на структуру проекта и интерфейс. Сначала соберу картину, затем предложу точечные улучшения с понятным результатом.'))

$size = [System.Windows.Size]::new($Width, $Height)
$workspace.Measure($size)
$workspace.Arrange([System.Windows.Rect]::new(0, 0, $Width, $Height))
$workspace.UpdateLayout()

$bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
    $Width, $Height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($workspace)
$encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
[void]$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
$stream = [System.IO.File]::Open($resolvedOutput, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
try { $encoder.Save($stream) }
finally { $stream.Dispose() }

Write-Output $resolvedOutput
