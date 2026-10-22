<#
.SYNOPSIS
Renders a CodexGUI interface view to PNG without launching CodexGUI.

.DESCRIPTION
Loads App.xaml resources and renders either Views/WorkspaceView.xaml with local preview messages
or the built-in Activity window in a standalone STA PowerShell process. It never starts CodexGUI
or the App Server.

.EXAMPLE
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1

.EXAMPLE
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -OutputPath .\preview\workspace.png -Width 1600 -Height 1000

.EXAMPLE
dotnet build --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View Activity -OutputPath .\preview\activity.png
#>
[CmdletBinding()]
param(
    [ValidateSet('Workspace', 'Activity')]
    [string]$View = 'Workspace',
    [string]$OutputPath,
    [int]$Width = 0,
    [int]$Height = 0
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne [Threading.ApartmentState]::STA) {
    throw 'Run this preview with: pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1'
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$projectRoot = Split-Path -Parent $PSScriptRoot
$appXamlPath = Join-Path $projectRoot 'App.xaml'
$workspaceXamlPath = Join-Path $projectRoot 'Views\WorkspaceView.xaml'
if (-not (Test-Path -LiteralPath $appXamlPath)) {
    throw 'App.xaml was not found beside this script.'
}

if ($Width -eq 0) { $Width = if ($View -eq 'Workspace') { 1440 } else { 580 } }
if ($Height -eq 0) { $Height = if ($View -eq 'Workspace') { 1000 } else { 600 } }
if ($Width -lt 400 -or $Width -gt 2560) { throw 'Width must be between 400 and 2560 pixels.' }
if ($Height -lt 320 -or $Height -gt 1800) { throw 'Height must be between 320 and 1800 pixels.' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $env:TEMP ("CodexGUI-{0}-preview.png" -f $View.ToLowerInvariant())
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

$visual = $null
if ($View -eq 'Workspace') {
    if (-not (Test-Path -LiteralPath $workspaceXamlPath)) {
        throw 'Views/WorkspaceView.xaml was not found beside this script.'
    }
    $workspace = [System.Windows.Markup.XamlReader]::Parse([System.IO.File]::ReadAllText($workspaceXamlPath))
    if ($workspace -isnot [System.Windows.Controls.Grid]) {
        throw 'WorkspaceView.xaml must have a Grid root element.'
    }
    $conversation = $workspace.FindName('Conversation')
    if ($conversation -isnot [System.Windows.Controls.StackPanel]) {
        throw 'The Conversation panel could not be found in WorkspaceView.xaml.'
    }
    $conversation.Children.Clear()
    [void]$conversation.Children.Add((New-PreviewMessage -IsUser $true -Text 'Проверь экран чата и предложи, как сделать его удобнее для долгой работы.'))
    [void]$conversation.Children.Add((New-PreviewMessage -IsUser $false -Text 'Я посмотрю на структуру проекта и интерфейс. Сначала соберу картину, затем предложу точечные улучшения с понятным результатом.'))
    $visual = $workspace
}
else {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build --no-restore to preview the Activity window.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $windowType = $assembly.GetType('CodexGUI.CodexActivityWindow', $true)
    $itemType = $assembly.GetType('CodexGUI.CodexActivityItem', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $callbackType = [System.Action[string]]
    $windowConstructor = $windowType.GetConstructor($flags, $null, [Type[]]@($callbackType), $null)
    $itemConstructor = $itemType.GetConstructor($flags, $null, [Type[]]@([string], [string], [string], [string], [string], [string]), $null)
    if ($null -eq $windowConstructor -or $null -eq $itemConstructor) {
        throw 'The Activity window preview constructors were not found in the built assembly.'
    }
    $openThread = [System.Action[string]]{ param([string]$threadId) }
    $window = $windowConstructor.Invoke([object[]]@($openThread))
    $window.Width = $Width
    $window.Height = $Height

    $previewItems = [Array]::CreateInstance($itemType, 3)
    $previewItems.SetValue($itemConstructor.Invoke([object[]]@('activity-waiting', 'Разрешить изменение файлов', 'CodexGUI', 'Ждёт ответа', 'Нужно ответить на запрос Codex', '#E6BC70')), 0)
    $previewItems.SetValue($itemConstructor.Invoke([object[]]@('activity-running', 'Проверь структуру проекта', 'CodexGUI', 'Выполняется', 'Codex выполняет задачу', '#92E5C7')), 1)
    $previewItems.SetValue($itemConstructor.Invoke([object[]]@('activity-unread', 'Обновить документацию', 'CrystalMystery', 'Новый ответ', 'Ответ завершённого чата не прочитан', '#92E5C7')), 2)
    $updateMethod = $windowType.GetMethod('UpdateItems', $flags)
    $methodArguments = [Array]::CreateInstance([object], 1)
    $methodArguments.SetValue($previewItems, 0)
    $updateMethod.Invoke($window, $methodArguments)

    $windowContent = $window.Content
    if ($null -eq $windowContent) {
        throw "The Activity window has no content in the built assembly at $assemblyPath. Rebuild CodexGUI before capturing this view."
    }
    $window.Content = $null
    $visual = [System.Windows.Controls.Grid]::new()
    $visual.Background = $application.Resources['Canvas']
    $visual.SetValue([System.Windows.Documents.TextElement]::ForegroundProperty, $application.Resources['TextMain'])
    [void]$visual.Children.Add($windowContent)
}

$size = [System.Windows.Size]::new($Width, $Height)
$visual.Measure($size)
$visual.Arrange([System.Windows.Rect]::new(0, 0, $Width, $Height))
$visual.UpdateLayout()

$bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
    $Width, $Height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($visual)
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
