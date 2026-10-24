<#
.SYNOPSIS
Renders a CodexGUI interface view to PNG without launching CodexGUI.

.DESCRIPTION
Loads Application/App.xaml resources and renders Views/WorkspaceView.xaml, the shared empty-chat welcome view,
the built-in Activity and Actions windows and action editor, the dark file, folder, or save picker, the automation
editor and its calendar, the marketplace-add dialog and source manager, the MCP server status and OAuth login windows,
or the Pull Request editor with local preview data in a standalone STA PowerShell process. It never starts CodexGUI or the App Server.

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1

.EXAMPLE
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -OutputPath .\preview\workspace.png -Width 1600 -Height 1000

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View Welcome -OutputPath .\preview\welcome.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View Activity -OutputPath .\preview\activity.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View Actions -OutputPath .\preview\actions.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View ActionEditor -OutputPath .\preview\action-editor.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View MarketplaceAdd -OutputPath .\preview\marketplace-add.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View MarketplaceManager -OutputPath .\preview\marketplace-manager.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View McpServers -OutputPath .\preview\mcp-servers.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View McpOAuthLogin -OutputPath .\preview\mcp-oauth-login.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View Setup -OutputPath .\preview\setup.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View PullRequest -OutputPath .\preview\pull-request.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View AutomationEditor -OutputPath .\preview\automation-editor.png

.EXAMPLE
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View Calendar -OutputPath .\preview\calendar.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View FilePicker -OutputPath .\preview\file-picker.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View FolderPicker -OutputPath .\preview\folder-picker.png

.EXAMPLE
dotnet build Build/CodexGUI.csproj --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View SavePicker -OutputPath .\preview\save-picker.png
#>
[CmdletBinding()]
param(
    [ValidateSet('Workspace', 'Welcome', 'Reference', 'Activity', 'Actions', 'ActionEditor', 'MarketplaceAdd', 'MarketplaceManager', 'McpServers', 'McpOAuthLogin', 'Setup', 'PullRequest', 'AutomationEditor', 'Calendar', 'FilePicker', 'FolderPicker', 'SavePicker')]
    [string]$View = 'Workspace',
    [string]$OutputPath,
    [switch]$VerifyLayout,
    [int]$Width = 0,
    [int]$Height = 0
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne [Threading.ApartmentState]::STA) {
    throw 'Run this preview with: pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1'
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$projectRoot = Split-Path -Parent $PSScriptRoot
$appXamlPath = Join-Path $projectRoot 'Application\App.xaml'
$workspaceXamlPath = Join-Path $projectRoot 'Views\WorkspaceView.xaml'
if (-not (Test-Path -LiteralPath $appXamlPath)) {
    throw 'Application/App.xaml was not found in the project.'
}

if ($Width -eq 0) { $Width = if ($View -in @('Workspace', 'Welcome', 'Reference')) { 1440 } elseif ($View -eq 'Actions') { 760 } elseif ($View -eq 'ActionEditor') { 620 } elseif ($View -in @('MarketplaceAdd', 'MarketplaceManager')) { 680 } elseif ($View -eq 'McpServers') { 840 } elseif ($View -eq 'McpOAuthLogin') { 520 } elseif ($View -eq 'Calendar') { 420 } elseif ($View -in @('FilePicker', 'FolderPicker', 'SavePicker')) { 760 } elseif ($View -in @('Setup', 'AutomationEditor')) { 700 } elseif ($View -eq 'PullRequest') { 680 } else { 580 } }
if ($Height -eq 0) { $Height = if ($View -in @('Workspace', 'Welcome', 'Reference')) { 1000 } elseif ($View -eq 'Actions') { 560 } elseif ($View -eq 'ActionEditor') { 570 } elseif ($View -eq 'MarketplaceAdd') { 520 } elseif ($View -eq 'MarketplaceManager') { 520 } elseif ($View -eq 'McpServers') { 600 } elseif ($View -eq 'McpOAuthLogin') { 360 } elseif ($View -eq 'Calendar') { 360 } elseif ($View -eq 'Setup') { 470 } elseif ($View -eq 'PullRequest') { 560 } elseif ($View -eq 'AutomationEditor') { 780 } elseif ($View -in @('FilePicker', 'FolderPicker', 'SavePicker')) { 590 } else { 600 } }
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
    throw 'Application/App.xaml does not contain Application.Resources.'
}
$resourcesXaml = @"
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:primitives="clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework">
$($resourceNode.InnerXml)
</ResourceDictionary>
"@
$application.Resources.MergedDictionaries.Add([System.Windows.Markup.XamlReader]::Parse($resourcesXaml))

$script:markdownRenderer = $null
$debugAssembly = Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'
$releaseAssembly = Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll'
$rendererAssembly = @($debugAssembly, $releaseAssembly) | Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { Get-Item -LiteralPath $_ } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$markdownSource = Join-Path $projectRoot 'UI\Chat\ChatMarkdownRenderer.cs'
$welcomeSource = Join-Path $projectRoot 'UI\Chat\ChatWelcomeView.cs'
$markdownAssemblyIsFresh = $rendererAssembly -and (Test-Path -LiteralPath $markdownSource) -and
    $rendererAssembly.LastWriteTime -ge (Get-Item -LiteralPath $markdownSource).LastWriteTime
$welcomeAssemblyIsFresh = $rendererAssembly -and (Test-Path -LiteralPath $welcomeSource) -and
    $rendererAssembly.LastWriteTime -ge (Get-Item -LiteralPath $welcomeSource).LastWriteTime
if ($markdownAssemblyIsFresh) {
    $assembly = [System.Reflection.Assembly]::LoadFrom($rendererAssembly.FullName)
    $rendererType = $assembly.GetType('CodexGUI.ChatMarkdownRenderer', $false)
    if ($rendererType) {
        $script:markdownRenderer = $rendererType.GetMethod(
            'Render', [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::Public)
    }
}

function New-PreviewMessage([bool]$IsUser, [string]$Text) {
    $row = [System.Windows.Controls.StackPanel]::new()
    $row.HorizontalAlignment = if ($IsUser) { [System.Windows.HorizontalAlignment]::Right } else { [System.Windows.HorizontalAlignment]::Left }
    $row.MaxWidth = if ($IsUser) { 760 } else { 900 }
    $row.Margin = [System.Windows.Thickness]::new(0, 14, 0, 18)

    if (-not $IsUser) {
        $header = [System.Windows.Controls.DockPanel]::new()
        $header.Margin = [System.Windows.Thickness]::new(4, 0, 0, 7)
        $copyButton = [System.Windows.Controls.Button]::new()
        $copyButton.Content = '⧉'
        $copyButton.ToolTip = 'Копировать ответ'
        $copyButton.Padding = [System.Windows.Thickness]::new(6, 2, 6, 2)
        $copyButton.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#8799A7')
        $copyButton.FontSize = 13
        $copyButton.VerticalAlignment = [System.Windows.VerticalAlignment]::Center
        [System.Windows.Controls.DockPanel]::SetDock($copyButton, [System.Windows.Controls.Dock]::Right)
        [void]$header.Children.Add($copyButton)
        $label = [System.Windows.Controls.TextBlock]::new()
        $label.Text = '✳'
        $label.FontSize = 11
        $label.FontWeight = [System.Windows.FontWeights]::SemiBold
        $label.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#92E5C7')
        $label.VerticalAlignment = [System.Windows.VerticalAlignment]::Center
        [void]$header.Children.Add($label)
        [void]$row.Children.Add($header)
    }

    $body = [System.Windows.Controls.TextBlock]::new()
    $body.Text = $Text
    $body.FontSize = 16
    $body.LineHeight = 27
    $body.TextWrapping = [System.Windows.TextWrapping]::Wrap
    $body.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString($(if ($IsUser) { '#EAF4F0' } else { '#ECF2F5' }))
    $displayBody = $body
    if (-not $IsUser -and $script:markdownRenderer) {
        $displayBody = $script:markdownRenderer.Invoke($null, [object[]]@($Text))
    }

    $bubble = [System.Windows.Controls.Border]::new()
    $bubble.Background = [System.Windows.Media.BrushConverter]::new().ConvertFromString($(if ($IsUser) { '#1A332E' } else { '#121B23' }))
    $bubble.BorderBrush = [System.Windows.Media.BrushConverter]::new().ConvertFromString($(if ($IsUser) { '#35594F' } else { '#263541' }))
    $bubble.BorderThickness = [System.Windows.Thickness]::new(1)
    if ($IsUser) {
        $bubble.CornerRadius = [System.Windows.CornerRadius]::new(18, 18, 6, 18)
        $bubble.Padding = [System.Windows.Thickness]::new(19, 15, 19, 15)
        $bubble.Child = $displayBody
        [void]$row.Children.Add($bubble)
    }
    else {
        $response = [System.Windows.Controls.Grid]::new()
        [void]$response.ColumnDefinitions.Add([System.Windows.Controls.ColumnDefinition]::new())
        $response.ColumnDefinitions[0].Width = [System.Windows.GridLength]::new(2)
        $bodyColumn = [System.Windows.Controls.ColumnDefinition]::new()
        $bodyColumn.Width = [System.Windows.GridLength]::new(1, [System.Windows.GridUnitType]::Star)
        [void]$response.ColumnDefinitions.Add($bodyColumn)
        $accent = [System.Windows.Controls.Border]::new()
        $accent.Background = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#42695E')
        $accent.CornerRadius = [System.Windows.CornerRadius]::new(2)
        $accent.Margin = [System.Windows.Thickness]::new(0, 3, 14, 3)
        [void]$response.Children.Add($accent)
        $bubble.Background = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#121B23')
        $bubble.CornerRadius = [System.Windows.CornerRadius]::new(5, 16, 16, 16)
        $bubble.Padding = [System.Windows.Thickness]::new(19, 16, 20, 17)
        $bubble.Child = $displayBody
        [System.Windows.Controls.Grid]::SetColumn($bubble, 1)
        [void]$response.Children.Add($bubble)
        [void]$row.Children.Add($response)
    }
    return $row
}

$visual = $null
if ($View -in @('Workspace', 'Welcome', 'Reference')) {
    if (-not (Test-Path -LiteralPath $workspaceXamlPath)) {
        throw 'Views/WorkspaceView.xaml was not found beside this script.'
    }
    $workspace = [System.Windows.Markup.XamlReader]::Parse([System.IO.File]::ReadAllText($workspaceXamlPath))
    if ($workspace -isnot [System.Windows.Controls.Grid]) {
        throw 'WorkspaceView.xaml must have a Grid root element.'
    }
    $workspace.ColumnDefinitions[0].Width = [System.Windows.GridLength]::new(0)
    $workspace.ColumnDefinitions[1].Width = [System.Windows.GridLength]::new($(if ($Width -lt 1260) { 0 } elseif ($Width -ge 1400) { 400 } else { 300 }))
    $workspace.ColumnDefinitions[3].Width = [System.Windows.GridLength]::new($(if ($Width -ge 1400) { 398 } else { 310 }))
    if ($Width -lt 1260) { $workspace.FindName('SidebarPanel').Visibility = 'Collapsed' }
    $dockMargin = if ($Width -ge 1400) { 148 } else { 12 }
    $workspace.FindName('WorkspaceDock').Margin = [System.Windows.Thickness]::new($dockMargin, 0, $dockMargin, 4)
    $workspace.FindName('WorkspaceDockGrid').ColumnDefinitions[0].Width = [System.Windows.GridLength]::new($(if ($Width -ge 1100) { 190 } else { 130 }))
    if ($Width -lt 1100) {
        $workspace.FindName('WorkspaceProjectButton').Width = 150
        $workspace.FindName('WorkspaceBranchButton').Width = 80
        $workspace.FindName('WorkspaceProjectPrefix').Visibility = 'Collapsed'
        $workspace.FindName('WorkspaceAgentStatus').Visibility = 'Collapsed'
        foreach ($name in @('WorkspaceChatsButton', 'WorkspaceAgentButton', 'WorkspaceToolsButton')) { $workspace.FindName($name).Margin = [System.Windows.Thickness]::new(0) }
        [System.Windows.Controls.Grid]::SetColumn($workspace.FindName('ComposerSettings'), 0)
        [System.Windows.Controls.Grid]::SetRow($workspace.FindName('ComposerSettings'), 1)
    }
    $workspace.FindName('WorkspaceTagline').Visibility = if ($Width -ge 1450) { 'Visible' } else { 'Collapsed' }
    $workspace.FindName('WorkspaceSearchButton').Width = if ($Width -ge 1450) { 304 } else { 160 }
    $workspace.FindName('WorkspaceHeaderGrid').ColumnDefinitions[0].Width = if ($Width -ge 1450) { [System.Windows.GridLength]::new(425) } else { [System.Windows.GridLength]::Auto }
    $workspace.FindName('WorkspaceHeaderControls').Margin = [System.Windows.Thickness]::new($(if ($Width -ge 1450) { 30 } else { 16 }), 0, 0, 0)
    $workspace.FindName('WorkspaceBranch').Text = '⑂  main'
    $treeRoot = [System.Windows.Controls.TreeViewItem]::new()
    $treeRoot.Header = '▱  CodexGUI'
    $treeRoot.IsExpanded = $true
    foreach ($directory in @('Application', 'AppServer', 'Infrastructure', 'UI', 'Views', 'Tests')) {
        $item = [System.Windows.Controls.TreeViewItem]::new()
        $item.Header = "▱  $directory"
        [void]$treeRoot.Items.Add($item)
    }
    [void]$workspace.FindName('FileTree').Items.Add($treeRoot)
    foreach ($name in @('AGENTS.md', '.codex/memory.md', '.codex/decisions.md', 'README.md')) {
        $label = [System.Windows.Controls.TextBlock]::new()
        $label.Text = "▤  $name"
        $label.FontSize = 11
        $label.Margin = [System.Windows.Thickness]::new(3, 7, 3, 7)
        [void]$workspace.FindName('WorkspaceContextList').Children.Add($label)
    }
    $conversation = $workspace.FindName('Conversation')
    if ($conversation -isnot [System.Windows.Controls.StackPanel]) {
        throw 'The Conversation panel could not be found in WorkspaceView.xaml.'
    }
    $goalButton = $workspace.FindName('ThreadGoalButton')
    $goalLabel = $workspace.FindName('ThreadGoalLabel')
    if ($goalButton -isnot [System.Windows.Controls.Button] -or
        $goalLabel -isnot [System.Windows.Controls.TextBlock]) {
        throw 'The current thread goal control could not be found in WorkspaceView.xaml.'
    }
    $promptInput = $workspace.FindName('PromptInput')
    if ($promptInput -isnot [System.Windows.Controls.TextBox] -or
        [System.Windows.Controls.SpellCheck]::GetIsEnabled($promptInput)) {
        throw 'The composer placeholder must not be spellchecked.'
    }
    $conversation.Children.Clear()
    if ($View -eq 'Workspace') {
        $goalButton.IsEnabled = $true
        $goalButton.ToolTip = "Активна`nНепрерывно улучшать CodexGUI"
        $goalLabel.Text = 'Цель · активна · Улучшать CodexGUI'
        [void]$conversation.Children.Add((New-PreviewMessage -IsUser $true -Text 'Проверь экран чата и предложи, как сделать его удобнее для долгой работы.'))
        $previewResponse = @'
Готово — подробности в [документации проекта](https://learn.chatgpt.com/docs/developers).

| Элемент | Результат |
| --- | --- |
| Таблица | Отформатирована |

```csharp
var ready = true;
```
'@
        [void]$conversation.Children.Add((New-PreviewMessage -IsUser $false -Text $previewResponse))
    }
    else {
        if ($workspace.FindName('ChatOverviewCard') -is [System.Windows.Controls.Border]) {
            $workspace.FindName('ChatOverviewCard').Visibility = [System.Windows.Visibility]::Collapsed
        }
        if (-not $welcomeAssemblyIsFresh) {
            throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview the Welcome screen.'
        }
        $assembly = [System.Reflection.Assembly]::LoadFrom($rendererAssembly.FullName)
        $welcomeType = $assembly.GetType('CodexGUI.ChatWelcomeView', $true)
        $flags = [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic
        $createMethod = $welcomeType.GetMethod('Create', $flags)
        if ($null -eq $createMethod) {
            throw 'The Welcome screen preview method was not found in the built assembly.'
        }
        $noSuggestionAction = [System.Action[string]]{ param([string]$prompt) }
        $welcome = $createMethod.Invoke($null, [object[]]@(
            'Привет! Я Codex.',
            'Ваш локальный ИИ-ассистент для разработки.',
            [string[]]@('Объясни архитектуру проекта', 'Найди и исправь ошибку', 'Добавь новую функциональность', 'Предложи улучшения'),
            $noSuggestionAction))
        [void]$conversation.Children.Add($welcome)
        $workspace.FindName('WorkspaceStage').RowDefinitions[0].Height = [System.Windows.GridLength]::new(0)
    }
    if ($rendererAssembly) {
        $assembly = [System.Reflection.Assembly]::LoadFrom($rendererAssembly.FullName)
        $flags = [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic
        $backdrop = $assembly.GetType('CodexGUI.WorkspaceBackdrop', $true).GetMethod('Create', $flags).Invoke($null, @())
        $workspace.Background = $backdrop
        $workspace.FindName('WorkspaceStage').Background = [System.Windows.Media.Brushes]::Transparent
        $nodes = $assembly.GetType('CodexGUI.WorkspaceProjectMap', $true).GetMethod('Read', $flags).Invoke($null, @([string]$projectRoot, 0))
        $selectNode = [System.Action[string]]{ param([string]$path) }
        [void]$assembly.GetType('CodexGUI.WorkspaceMapView', $true).GetMethod('Render', $flags).Invoke($null, @($workspace.FindName('ProjectMap'), $nodes, [string]$projectRoot, 1.0, $selectNode))
    }
    if ($View -eq 'Reference') {
        . (Join-Path $PSScriptRoot 'WorkspaceReferenceFixture.ps1')
        Set-WorkspaceReferenceFixture -Workspace $workspace -Assembly $assembly
    }
    $visual = $workspace
}
elseif ($View -eq 'Activity') {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview the Activity window.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $windowType = $assembly.GetType('CodexGUI.CodexActivityWindow', $true)
    $itemType = $assembly.GetType('CodexGUI.CodexActivityItem', $true)
    $activityEventType = $assembly.GetType('CodexGUI.CodexActivityEvent', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $callbackType = [System.Action[string]]
    $windowConstructor = $windowType.GetConstructor($flags, $null, [Type[]]@($callbackType), $null)
    $itemConstructor = $itemType.GetConstructor($flags, $null, [Type[]]@([string], [string], [string], [string], [string], [string]), $null)
    $activityEventConstructor = $activityEventType.GetConstructor($flags, $null, [Type[]]@([string], [string], [bool]), $null)
    $recentEventsProperty = $itemType.GetProperty('RecentEvents')
    if ($null -eq $windowConstructor -or $null -eq $itemConstructor -or $null -eq $activityEventConstructor -or $null -eq $recentEventsProperty) {
        throw 'The Activity window preview constructors were not found in the built assembly.'
    }
    $openThread = [System.Action[string]]{ param([string]$threadId) }
    $window = $windowConstructor.Invoke([object[]]@($openThread))
    $window.Width = $Width
    $window.Height = $Height

    $previewItems = [Array]::CreateInstance($itemType, 3)
    $waitingItem = $itemConstructor.Invoke([object[]]@('activity-waiting', 'Разрешить изменение файлов', 'CodexGUI', 'Ждёт ответа', 'Нужно ответить на запрос Codex', '#E6BC70'))
    $waitingEvents = [Array]::CreateInstance($activityEventType, 1)
    $waitingEvents.SetValue($activityEventConstructor.Invoke([object[]]@('●  Вызов инструмента · выполняется', 'Нужно разрешение на изменение файлов', $false)), 0)
    $recentEventsProperty.SetValue($waitingItem, $waitingEvents)
    $previewItems.SetValue($waitingItem, 0)

    $runningItem = $itemConstructor.Invoke([object[]]@('activity-running', 'Проверь структуру проекта', 'CodexGUI', 'Выполняется', 'Codex выполняет задачу', '#92E5C7'))
    $runningEvents = [Array]::CreateInstance($activityEventType, 3)
    $runningEvents.SetValue($activityEventConstructor.Invoke([object[]]@('✓  Команда · завершено', 'git status --short', $false)), 0)
    $runningEvents.SetValue($activityEventConstructor.Invoke([object[]]@('✓  Поиск в интернете · завершено', 'Найти документацию по App Server', $false)), 1)
    $runningEvents.SetValue($activityEventConstructor.Invoke([object[]]@('●  Команда · выполняется', 'dotnet build Build/CodexGUI.csproj --no-restore', $false)), 2)
    $recentEventsProperty.SetValue($runningItem, $runningEvents)
    $previewItems.SetValue($runningItem, 1)

    $unreadItem = $itemConstructor.Invoke([object[]]@('activity-unread', 'Обновить документацию', 'CrystalMystery', 'Новый ответ', 'Ответ завершённого чата не прочитан', '#92E5C7'))
    $unreadEvents = [Array]::CreateInstance($activityEventType, 2)
    $unreadEvents.SetValue($activityEventConstructor.Invoke([object[]]@('✓  Изменение файлов · завершено', 'README.md, docs/setup.md', $false)), 0)
    $unreadEvents.SetValue($activityEventConstructor.Invoke([object[]]@('✓  Команда · завершено', 'git diff --check', $false)), 1)
    $recentEventsProperty.SetValue($unreadItem, $unreadEvents)
    $previewItems.SetValue($unreadItem, 2)
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
elseif ($View -eq 'Actions') {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview the Actions window.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $windowType = $assembly.GetType('CodexGUI.CodexActionsWindow', $true)
    $itemType = $assembly.GetType('CodexGUI.CodexActionDefinition', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $itemListType = [System.Collections.Generic.List``1].MakeGenericType($itemType)
    $items = [Activator]::CreateInstance($itemListType)
    $projectItems = [Activator]::CreateInstance($itemListType)
    foreach ($previewAction in @(
        @('Проверить изменения', 'Изучи текущие изменения, найди ошибки и предложи точечные исправления. Проверь diff и релевантные сценарии.'),
        @('Объяснить файл', 'Объясни назначение файла, его основные части и связи с остальным проектом.')
    )) {
        $item = [Activator]::CreateInstance($itemType)
        $itemType.GetProperty('Name').SetValue($item, $previewAction[0])
        $itemType.GetProperty('Prompt').SetValue($item, $previewAction[1])
        $itemListType.GetMethod('Add').Invoke($items, [object[]]@($item)) | Out-Null
    }
    $projectAction = [Activator]::CreateInstance($itemType)
    $itemType.GetProperty('Name').SetValue($projectAction, 'Обновить документацию')
    $itemType.GetProperty('Command').SetValue($projectAction, 'dotnet build Build/CodexGUI.csproj --no-restore')
    if (-not $projectAction.HasValidPayload) {
        throw 'A PowerShell command action was not accepted as a valid payload.'
    }
    $itemListType.GetMethod('Add').Invoke($projectItems, [object[]]@($projectAction)) | Out-Null
    $legacyAction = [Activator]::CreateInstance($itemType)
    $itemType.GetProperty('Prompt').SetValue($legacyAction, 'Existing prompt action')
    if (-not $legacyAction.HasValidPayload -or $legacyAction.Command -ne '') {
        throw 'A legacy prompt action without a Command field was not kept compatible.'
    }
    $emptyAction = [Activator]::CreateInstance($itemType)
    $ambiguousAction = [Activator]::CreateInstance($itemType)
    $itemType.GetProperty('Prompt').SetValue($ambiguousAction, 'Prompt')
    $itemType.GetProperty('Command').SetValue($ambiguousAction, 'Write-Output smoke')
    if ($emptyAction.HasValidPayload -or $ambiguousAction.HasValidPayload) {
        throw 'Empty and mixed prompt/command actions must be rejected.'
    }
    $save = [Func[bool]]{ $true }
    $saveProject = [Func[bool]]{ $true }
    $insert = [System.Action[string]]{ param([string]$prompt) }
    $runCommand = [System.Action[string, string]]{ param([string]$name, [string]$command) }
    $constructor = $windowType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 7 } | Select-Object -First 1
    if ($null -eq $constructor) {
        throw 'The Actions window preview constructor was not found in the built assembly.'
    }
    $window = $constructor.Invoke([object[]]@($items, $projectItems, $save, $saveProject, $true, $insert, $runCommand))
    $actionList = $windowType.GetField('_list', $flags).GetValue($window)
    for ($index = 0; $index -lt $actionList.Items.Count; $index++) {
        $actionList.SelectedIndex = $index
        foreach ($buttonName in @('_editButton', '_deleteButton', '_actionButton', '_copyButton')) {
            if (-not $windowType.GetField($buttonName, $flags).GetValue($window).IsEnabled) {
                throw "Selecting action $index did not enable $buttonName."
            }
        }
        if ($actionList.SelectedItem.Tag.Action.IsCommand -and
            $windowType.GetField('_actionButton', $flags).GetValue($window).Content -ne 'Запустить в PowerShell') {
            throw 'Selecting a PowerShell action did not label the action button for execution.'
        }
    }
    $actionList.SelectedIndex = -1
    $searchBox = $windowType.GetField('_search', $flags).GetValue($window)
    $searchBox.Text = 'Объяснить файл'
    if ($actionList.Items.Count -ne 1) {
        throw 'Filtering actions by name did not return the matching item.'
    }
    $searchBox.Text = 'связи с остальным'
    if ($actionList.Items.Count -ne 1) {
        throw 'Filtering actions by prompt text did not return the matching item.'
    }
    $searchBox.Text = '--no-restore'
    if ($actionList.Items.Count -ne 1) {
        throw 'Filtering actions by PowerShell command did not return the matching item.'
    }
    $searchBox.Text = ''
    if ($actionList.Items.Count -ne 3) {
        throw 'Clearing the Actions filter did not restore all items.'
    }

    $copyMethod = $windowType.GetMethod('CopySelected', $flags)
    $testSave = [Func[bool]]{ $true }
    $testInsert = [System.Action[string]]{ param([string]$prompt) }
    $testLocalItems = [Activator]::CreateInstance($itemListType)
    $testProjectItems = [Activator]::CreateInstance($itemListType)
    $testLocalAction = [Activator]::CreateInstance($itemType)
    $itemType.GetProperty('Name').SetValue($testLocalAction, 'Personal smoke action')
    $itemType.GetProperty('Prompt').SetValue($testLocalAction, 'Verify a local action can be copied to a project.')
    $itemListType.GetMethod('Add').Invoke($testLocalItems, [object[]]@($testLocalAction)) | Out-Null
    $testRunCommand = [System.Action[string, string]]{ param([string]$name, [string]$command) }
    $testWindow = $constructor.Invoke([object[]]@($testLocalItems, $testProjectItems, $testSave, $testSave, $true, $testInsert, $testRunCommand))
    $testList = $windowType.GetField('_list', $flags).GetValue($testWindow)
    $testList.SelectedIndex = 0
    $copyButton = $windowType.GetField('_copyButton', $flags).GetValue($testWindow)
    if (-not $copyButton.IsEnabled -or $copyButton.Content -ne 'Копия в проект') {
        throw 'A personal action did not offer copying to the project.'
    }
    $copyMethod.Invoke($testWindow, [object[]]@()) | Out-Null
    if ($testLocalItems.Count -ne 1 -or
        $testProjectItems.Count -ne 1 -or
        $testProjectItems[0].Id -eq $testLocalAction.Id -or
        $testProjectItems[0].Name -ne $testLocalAction.Name -or
        $testProjectItems[0].Prompt -ne $testLocalAction.Prompt) {
        throw 'Copying a personal action to the project did not preserve both independent entries.'
    }

    $reverseLocalItems = [Activator]::CreateInstance($itemListType)
    $reverseProjectItems = [Activator]::CreateInstance($itemListType)
    $testProjectAction = [Activator]::CreateInstance($itemType)
    $itemType.GetProperty('Name').SetValue($testProjectAction, 'Project smoke action')
    $itemType.GetProperty('Command').SetValue($testProjectAction, 'Write-Output "smoke"')
    $itemListType.GetMethod('Add').Invoke($reverseProjectItems, [object[]]@($testProjectAction)) | Out-Null
    $reverseWindow = $constructor.Invoke([object[]]@($reverseLocalItems, $reverseProjectItems, $testSave, $testSave, $true, $testInsert, $testRunCommand))
    $reverseList = $windowType.GetField('_list', $flags).GetValue($reverseWindow)
    $reverseList.SelectedIndex = 0
    $reverseCopyButton = $windowType.GetField('_copyButton', $flags).GetValue($reverseWindow)
    if (-not $reverseCopyButton.IsEnabled -or $reverseCopyButton.Content -ne 'Копия в личные') {
        throw 'A project action did not offer copying to the personal library.'
    }
    $copyMethod.Invoke($reverseWindow, [object[]]@()) | Out-Null
    if ($reverseProjectItems.Count -ne 1 -or
        $reverseLocalItems.Count -ne 1 -or
        $reverseLocalItems[0].Id -eq $testProjectAction.Id -or
        $reverseLocalItems[0].Name -ne $testProjectAction.Name -or
        $reverseLocalItems[0].Command -ne $testProjectAction.Command) {
        throw 'Copying a project action to the personal library did not preserve both independent entries.'
    }

    $filterLocalItems = [Activator]::CreateInstance($itemListType)
    $filterProjectItems = [Activator]::CreateInstance($itemListType)
    $filteredAction = [Activator]::CreateInstance($itemType)
    $itemType.GetProperty('Name').SetValue($filteredAction, 'Existing action')
    $itemType.GetProperty('Prompt').SetValue($filteredAction, 'Searchable phrase')
    $itemListType.GetMethod('Add').Invoke($filterLocalItems, [object[]]@($filteredAction)) | Out-Null
    $filterWindow = $constructor.Invoke([object[]]@($filterLocalItems, $filterProjectItems, $testSave, $testSave, $true, $testInsert, $testRunCommand))
    $filterBox = $windowType.GetField('_search', $flags).GetValue($filterWindow)
    $filterBox.Text = 'Searchable phrase'
    $filterList = $windowType.GetField('_list', $flags).GetValue($filterWindow)
    if ($filterList.Items.Count -ne 1) { throw 'The pre-mutation Actions filter did not match its existing item.' }
    $newAction = [Activator]::CreateInstance($itemType)
    $itemType.GetProperty('Name').SetValue($newAction, 'New project action')
    $itemType.GetProperty('Prompt').SetValue($newAction, 'A different phrase')
    $itemListType.GetMethod('Add').Invoke($filterProjectItems, [object[]]@($newAction)) | Out-Null
    $windowType.GetMethod('ClearSearchFilter', $flags).Invoke($filterWindow, [object[]]@()) | Out-Null
    $windowType.GetMethod('RefreshList', $flags).Invoke($filterWindow, [object[]]@($newAction.Id, $true)) | Out-Null
    $selectedEntry = $filterList.SelectedItem.Tag
    $selectedAction = $selectedEntry.GetType().GetProperty('Action').GetValue($selectedEntry)
    if ($filterBox.Text.Length -ne 0 -or $filterList.Items.Count -ne 2 -or $selectedAction.Id -ne $newAction.Id) {
        throw 'Saving a new action did not clear the filter and reveal the saved item.'
    }

    $window.Width = $Width
    $window.Height = $Height
    $windowContent = $window.Content
    if ($null -eq $windowContent) {
        throw "The Actions window has no content in the built assembly at $assemblyPath. Rebuild CodexGUI before capturing this view."
    }
    $window.Content = $null
    $visual = [System.Windows.Controls.Grid]::new()
    $visual.Background = $application.Resources['Canvas']
    $visual.SetValue([System.Windows.Documents.TextElement]::ForegroundProperty, $application.Resources['TextMain'])
    [void]$visual.Children.Add($windowContent)
}
elseif ($View -eq 'ActionEditor') {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview the action editor.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $windowType = $assembly.GetType('CodexGUI.CodexActionEditorWindow', $true)
    $itemType = $assembly.GetType('CodexGUI.CodexActionDefinition', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $source = [Activator]::CreateInstance($itemType)
    $itemType.GetProperty('Name').SetValue($source, 'Собрать приложение')
    $itemType.GetProperty('Command').SetValue($source, 'dotnet build Build/CodexGUI.csproj --no-restore')
    $constructor = $windowType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 1 } | Select-Object -First 1
    if ($null -eq $constructor) {
        throw 'The action editor preview constructor was not found in the built assembly.'
    }
    $window = $constructor.Invoke([object[]]@($source))
    $selector = $windowType.GetField('_type', $flags).GetValue($window)
    $promptEditor = $windowType.GetField('_prompt', $flags).GetValue($window)
    $commandEditor = $windowType.GetField('_command', $flags).GetValue($window)
    if ($selector.SelectedIndex -ne 1 -or
        $promptEditor.Visibility -ne [System.Windows.Visibility]::Collapsed -or
        $commandEditor.Visibility -ne [System.Windows.Visibility]::Visible) {
        throw 'The command editor did not select and display the PowerShell command field.'
    }
    $window.Width = $Width
    $window.Height = $Height
    $windowContent = $window.Content
    if ($null -eq $windowContent) {
        throw "The action editor has no content in the built assembly at $assemblyPath. Rebuild CodexGUI before capturing this view."
    }
    $window.Content = $null
    $visual = [System.Windows.Controls.Grid]::new()
    $visual.Background = $application.Resources['Canvas']
    $visual.SetValue([System.Windows.Documents.TextElement]::ForegroundProperty, $application.Resources['TextMain'])
    [void]$visual.Children.Add($windowContent)
}
elseif ($View -eq 'MarketplaceAdd') {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview the marketplace editor.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $windowType = $assembly.GetType('CodexGUI.CodexMarketplaceAddWindow', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $constructor = $windowType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 0 } | Select-Object -First 1
    if ($null -eq $constructor) {
        throw 'The marketplace editor preview constructor was not found in the built assembly.'
    }
    $window = $constructor.Invoke([object[]]@())
    $window.Width = $Width
    $window.Height = $Height
    $windowContent = $window.Content
    if ($null -eq $windowContent) {
        throw "The marketplace editor has no content in the built assembly at $assemblyPath. Rebuild CodexGUI before capturing this view."
    }
    $window.Content = $null
    $visual = [System.Windows.Controls.Grid]::new()
    $visual.Background = $application.Resources['Canvas']
    $visual.SetValue([System.Windows.Documents.TextElement]::ForegroundProperty, $application.Resources['TextMain'])
    [void]$visual.Children.Add($windowContent)
}
elseif ($View -eq 'MarketplaceManager') {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview the marketplace manager.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $catalogType = $assembly.GetType('CodexGUI.CodexMarketplaceCatalog', $true)
    $staticFlags = [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $fixtures = @{ marketplaces = @(
        @{ name = 'sample-tools'; root = 'C:\Codex\marketplaces\sample-tools' },
        @{ name = 'local-workflows'; root = 'C:\Codex\marketplaces\local-workflows' }
    ) }
    $parseMethod = $catalogType.GetMethod('Parse', $staticFlags)
    $fixtureJson = [string]($fixtures | ConvertTo-Json -Depth 5)
    $marketplaces = $parseMethod.Invoke($null, [object[]]@($fixtureJson))
    if ($marketplaces.Count -ne 2 -or $marketplaces[0].Name -ne 'local-workflows') {
        throw 'Marketplace manager preview could not parse and sort its local fixtures.'
    }

    $windowType = $assembly.GetType('CodexGUI.CodexMarketplaceManagerWindow', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $constructor = $windowType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 2 } | Select-Object -First 1
    if ($null -eq $constructor) {
        throw 'The marketplace manager preview constructor was not found in the built assembly.'
    }
    $constructorArguments = [object[]]::new(2)
    $constructorArguments[0] = $marketplaces
    $constructorArguments[1] = $null
    $window = $constructor.Invoke($constructorArguments)
    $list = $windowType.GetField('_list', $flags).GetValue($window)
    $remove = $windowType.GetField('_remove', $flags).GetValue($window)
    $list.SelectedIndex = 0
    if (-not $remove.IsEnabled) { throw 'Selecting a marketplace did not enable its remove action.' }
    $list.SelectedIndex = -1
    if ($remove.IsEnabled) { throw 'Clearing the marketplace selection left its remove action enabled.' }
    $list.SelectedIndex = 0
    $window.Width = $Width
    $window.Height = $Height
    $windowContent = $window.Content
    if ($null -eq $windowContent) {
        throw "The marketplace manager has no content in the built assembly at $assemblyPath. Rebuild CodexGUI before capturing this view."
    }
    $window.Content = $null
    $visual = [System.Windows.Controls.Grid]::new()
    $visual.Background = $application.Resources['Canvas']
    $visual.SetValue([System.Windows.Documents.TextElement]::ForegroundProperty, $application.Resources['TextMain'])
    [void]$visual.Children.Add($windowContent)
}
elseif ($View -eq 'McpServers') {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview MCP server status.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $catalogType = $assembly.GetType('CodexGUI.CodexMcpServerStatusCatalog', $true)
    $staticFlags = [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $fixtures = @(
        @{ name = 'calendar'; runtimeStatus = 'connected'; authStatus = 'oAuth'; tools = @{ create_event = @{}; find_events = @{} }; toolsError = $null; serverInfo = @{ title = 'Calendar'; version = '1.2.0' }; httpOrigin = 'https://calendar.example.test' },
        @{ name = 'issues'; runtimeStatus = 'failed'; authStatus = 'notLoggedIn'; tools = @{}; toolsError = 'MCP startup failed'; serverInfo = $null; httpOrigin = 'https://issues.example.test' },
        @{ name = 'local-worker'; runtimeStatus = $null; authStatus = 'notLoggedIn'; tools = @{}; toolsError = $null; serverInfo = @{ title = 'Local worker'; version = '1.0' }; httpOrigin = $null }
    )
    $fixtureJson = [string]($fixtures | ConvertTo-Json -Depth 6)
    $parseMethod = $catalogType.GetMethod('Parse', $staticFlags)
    $statuses = $parseMethod.Invoke($null, [object[]]@($fixtureJson))
    if ($statuses.Count -ne 3 -or $statuses[0].Name -ne 'calendar' -or $statuses[1].StateLabel -ne 'Ошибка проверки') {
        throw 'MCP server status preview could not parse and sort its local fixtures.'
    }

    $windowType = $assembly.GetType('CodexGUI.CodexMcpServerStatusWindow', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $constructor = $windowType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 1 } | Select-Object -First 1
    if ($null -eq $constructor) {
        throw 'The MCP server status preview constructor was not found in the built assembly.'
    }
    $constructorArguments = [object[]]::new(1)
    $constructorArguments[0] = 'preview-codex-app-server.exe'
    $window = $constructor.Invoke($constructorArguments)
    $windowType.GetField('_items', $flags).SetValue($window, $statuses)
    $windowType.GetMethod('RefreshList', $flags).Invoke($window, [object[]]@($null)) | Out-Null
    $list = $windowType.GetField('_servers', $flags).GetValue($window)
    $login = $windowType.GetField('_login', $flags).GetValue($window)
    $isVirtualizing = [System.Windows.Controls.VirtualizingPanel]::GetIsVirtualizing($list)
    $virtualizationMode = [System.Windows.Controls.VirtualizingPanel]::GetVirtualizationMode($list)
    if (-not $isVirtualizing -or $virtualizationMode -ne [System.Windows.Controls.VirtualizationMode]::Recycling) {
        throw 'The MCP server list is not configured for virtualized container recycling.'
    }
    $search = $windowType.GetField('_search', $flags).GetValue($window)
    $search.Text = 'create_event'
    if ($list.Items.Count -ne 1 -or $list.SelectedItem.Name -ne 'calendar') {
        throw 'Searching for an MCP tool did not filter to its server.'
    }
    $search.Text = 'issues'
    if ($list.Items.Count -ne 1 -or $list.SelectedItem.Name -ne 'issues' -or -not $login.IsEnabled) {
        throw 'The MCP server filter did not preserve the sign-in action for the matching server.'
    }
    $search.Text = 'local-worker'
    if ($list.Items.Count -ne 1 -or $list.SelectedItem.Name -ne 'local-worker' -or $login.IsEnabled) {
        throw 'OAuth sign-in was offered for a non-HTTP MCP server.'
    }
    $search.Clear()
    $list.SelectedIndex = 1
    if (-not $login.IsEnabled) { throw 'Selecting a server that requires OAuth did not enable its sign-in action.' }
    $windowType.GetMethod('ShowSelectedDetails', $flags).Invoke($window, [object[]]@()) | Out-Null
    $windowType.GetField('_status', $flags).GetValue($window).Text = 'Проверка завершена. Серверов: 3.'
    $window.Width = $Width
    $window.Height = $Height
    $windowContent = $window.Content
    if ($null -eq $windowContent) {
        throw "The MCP server status window has no content in the built assembly at $assemblyPath. Rebuild CodexGUI before capturing this view."
    }
    $window.Content = $null
    $visual = [System.Windows.Controls.Grid]::new()
    $visual.Background = $application.Resources['Canvas']
    $visual.SetValue([System.Windows.Documents.TextElement]::ForegroundProperty, $application.Resources['TextMain'])
    [void]$visual.Children.Add($windowContent)
}
elseif ($View -eq 'McpOAuthLogin') {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview MCP OAuth sign-in.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $windowType = $assembly.GetType('CodexGUI.CodexMcpServerOAuthLoginWindow', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $constructor = $windowType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 2 } | Select-Object -First 1
    if ($null -eq $constructor) {
        throw 'The MCP OAuth sign-in preview constructor was not found in the built assembly.'
    }
    $window = $constructor.Invoke([object[]]@('sample-calendar', 'https://login.example.test/oauth/authorize?state=preview'))
    $window.Width = $Width
    $window.Height = $Height
    $windowType.GetField('_status', $flags).GetValue($window).Text = 'Страница входа откроется только после нажатия кнопки.'
    $windowContent = $window.Content
    if ($null -eq $windowContent) {
        throw "The MCP OAuth sign-in window has no content in the built assembly at $assemblyPath. Rebuild CodexGUI before capturing this view."
    }
    $window.Content = $null
    $visual = [System.Windows.Controls.Grid]::new()
    $visual.Background = $application.Resources['Canvas']
    $visual.SetValue([System.Windows.Documents.TextElement]::ForegroundProperty, $application.Resources['TextMain'])
    [void]$visual.Children.Add($windowContent)
}
elseif ($View -eq 'PullRequest') {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview the Pull Request editor.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $windowType = $assembly.GetType('CodexGUI.CodexPullRequestWindow', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $constructor = $windowType.GetConstructor($flags, $null, [Type[]]@([string]), $null)
    if ($null -eq $constructor) {
        throw 'The Pull Request editor preview constructor was not found in the built assembly.'
    }
    $window = $constructor.Invoke([object[]]@('feature/improve-chat'))
    $window.Width = $Width
    $window.Height = $Height
    $windowContent = $window.Content
    if ($null -eq $windowContent) {
        throw "The Pull Request editor has no content in the built assembly at $assemblyPath. Rebuild CodexGUI before capturing this view."
    }
    $window.Content = $null
    $visual = [System.Windows.Controls.Grid]::new()
    $visual.Background = $application.Resources['Canvas']
    $visual.SetValue([System.Windows.Documents.TextElement]::ForegroundProperty, $application.Resources['TextMain'])
    [void]$visual.Children.Add($windowContent)
}
elseif ($View -eq 'AutomationEditor') {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview the automation editor.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $projectType = $assembly.GetType('CodexGUI.CodexAutomationProject', $true)
    $modelType = $assembly.GetType('CodexGUI.ModelOption', $true)
    $windowType = $assembly.GetType('CodexGUI.CodexAutomationEditorWindow', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $projectConstructor = $projectType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 4 } | Select-Object -First 1
    $modelConstructor = $modelType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 5 } | Select-Object -First 1
    $windowConstructor = $windowType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 6 } | Select-Object -First 1
    if ($null -eq $projectConstructor -or $null -eq $modelConstructor -or $null -eq $windowConstructor) {
        throw 'The automation editor preview constructors were not found in the built assembly.'
    }

    $projects = [Array]::CreateInstance($projectType, 2)
    $projects.SetValue($projectConstructor.Invoke([object[]]@('preview-codexgui', 'CodexGUI', 'C:\Projects\CodexGUI', $true)), 0)
    $projects.SetValue($projectConstructor.Invoke([object[]]@('preview-sample', 'Demo project', 'C:\Projects\Demo', $true)), 1)
    $chats = [Array]::CreateInstance($assembly.GetType('CodexGUI.CodexAutomationChat', $true), 0)
    $models = [Array]::CreateInstance($modelType, 1)
    $models.SetValue($modelConstructor.Invoke([object[]]@('gpt-6-luna', 'GPT-6 Luna', 'high', [string[]]@('low', 'medium', 'high', 'xhigh'), 0)), 0)
    $window = $windowConstructor.Invoke([object[]]@($projects, $chats, $models, $null, 'preview-codexgui', $true))
    $window.Width = $Width
    $window.Height = $Height
    $nameInput = $windowType.GetField('_name', $flags).GetValue($window)
    $nameInput.Text = 'Еженедельный обзор проекта'
    $promptInput = $windowType.GetField('_prompt', $flags).GetValue($window)
    $promptInput.Text = 'Изучи изменения проекта, найди возможные ошибки и кратко перечисли риски.'
    $cadenceInput = $windowType.GetField('_cadence', $flags).GetValue($window)
    $cadenceInput.SelectedIndex = 4
    $ruleInput = $windowType.GetField('_ruleText', $flags).GetValue($window)
    $ruleInput.Text = 'FREQ=WEEKLY;BYDAY=MO;BYHOUR=9;BYMINUTE=0'
    $windowContent = $window.Content
    if ($null -eq $windowContent) {
        throw "The automation editor has no content in the built assembly at $assemblyPath. Rebuild CodexGUI before capturing this view."
    }
    $window.Content = $null
    $visual = [System.Windows.Controls.Grid]::new()
    $visual.Background = $application.Resources['Canvas']
    $visual.SetValue([System.Windows.Documents.TextElement]::ForegroundProperty, $application.Resources['TextMain'])
    [void]$visual.Children.Add($windowContent)
}
elseif ($View -eq 'Calendar') {
    $visual = [System.Windows.Controls.Grid]::new()
    $visual.Margin = [System.Windows.Thickness]::new(8)
    $visual.Background = $application.Resources['Canvas']
    $calendar = [System.Windows.Controls.Calendar]::new()
    $calendar.DisplayDate = [DateTime]::Today
    $calendar.SelectedDate = [DateTime]::Today
    $calendar.IsTodayHighlighted = $true
    [void]$visual.Children.Add($calendar)
}
elseif ($View -in @('FilePicker', 'FolderPicker', 'SavePicker')) {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview the file picker.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $windowType = $assembly.GetType('CodexGUI.CodexFilePickerWindow', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $constructor = $windowType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 6 } | Select-Object -First 1
    if ($null -eq $constructor) {
        throw 'The file picker preview constructor was not found in the built assembly.'
    }
    $mode = if ($View -eq 'FolderPicker') { 'folder' } elseif ($View -eq 'SavePicker') { 'save' } else { 'open' }
    $title = if ($View -eq 'FolderPicker') { 'Выберите папку' } elseif ($View -eq 'SavePicker') { 'Сохранить скриншот' } else { 'Выберите файл' }
    $defaultExtension = if ($View -eq 'SavePicker') { '.png' } else { '' }
    $fileName = if ($View -eq 'SavePicker') { 'CodexGUI-preview.png' } else { '' }
    $pickerArguments = [Array]::CreateInstance([object], 6)
    $pickerArguments.SetValue($title, 0)
    $pickerArguments.SetValue($projectRoot, 1)
    $pickerArguments.SetValue([string[]]@(), 2)
    $pickerArguments.SetValue($defaultExtension, 3)
    $pickerArguments.SetValue($fileName, 4)
    $pickerArguments.SetValue($mode, 5)
    $window = $constructor.Invoke($pickerArguments)
    $window.Width = $Width
    $window.Height = $Height
    $windowContent = $window.Content
    if ($null -eq $windowContent) {
        throw "The file picker has no content in the built assembly at $assemblyPath. Rebuild CodexGUI before capturing this view."
    }
    $window.Content = $null
    $visual = [System.Windows.Controls.Grid]::new()
    $visual.Background = $application.Resources['Canvas']
    $visual.SetValue([System.Windows.Documents.TextElement]::ForegroundProperty, $application.Resources['TextMain'])
    [void]$visual.Children.Add($windowContent)
}
else {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'Build\bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'Build\bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build Build/CodexGUI.csproj --no-restore to preview the worktree setup window.'
    }

    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $windowType = $assembly.GetType('CodexGUI.CodexWorktreeSetupWindow', $true)
    $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic
    $constructor = $windowType.GetConstructor($flags, $null, [Type[]]@([System.Windows.Window], [string], [string]), $null)
    if ($null -eq $constructor) {
        throw 'The worktree setup window preview constructor was not found in the built assembly.'
    }
    $window = $constructor.Invoke([object[]]@($null, 'C:\Projects\CodexGUI\worktree', 'C:\Projects\CodexGUI\worktree\scripts\setup.ps1'))
    $window.Width = $Width
    $window.Height = $Height
    $windowType.GetField('_status', $flags).GetValue($window).Text = 'Устанавливаю зависимости…'
    $windowType.GetField('_output', $flags).GetValue($window).Text = "Проверка окружения...`r`nВосстановление пакетов...`r`n"
    $windowContent = $window.Content
    if ($null -eq $windowContent) {
        throw "The worktree setup window has no content in the built assembly at $assemblyPath. Rebuild CodexGUI before capturing this view."
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

if ($VerifyLayout -and $View -in @('Workspace', 'Welcome', 'Reference')) {
    function Get-PreviewBounds([string]$Name) {
        $element = $visual.FindName($Name)
        if ($null -eq $element) { throw "Layout control not found: $Name" }
        return $element.TransformToAncestor($visual).TransformBounds([System.Windows.Rect]::new(0, 0, $element.ActualWidth, $element.ActualHeight))
    }
    if ($Width -eq 1585 -and $Height -eq 992) {
        $anchors = @{
            ProjectStructureCard = @(16, 80, 384, 378)
            ProjectMapCard = @(16, 468, 384, 390)
            GoalCard = @(1187, 81, 386, 280)
            ContextMemoryCard = @(1187, 373, 386, 214)
            QuickToolsCard = @(1187, 598, 386, 226)
            WorkspaceDock = @(148, 853, 1289, 135)
        }
        foreach ($entry in $anchors.GetEnumerator()) {
            $actual = Get-PreviewBounds $entry.Key
            $values = @($actual.X, $actual.Y, $actual.Width, $actual.Height)
            for ($i = 0; $i -lt 4; $i++) {
                if ([Math]::Abs($values[$i] - $entry.Value[$i]) -gt 2) {
                    throw "Reference layout mismatch for $($entry.Key): $actual; expected $($entry.Value -join ', ')"
                }
            }
        }
    }
    $composer = Get-PreviewBounds 'ComposerBorder'
    $input = Get-PreviewBounds 'PromptInput'
    $send = Get-PreviewBounds 'SendButton'
    if (-not $composer.Contains($input) -or -not $composer.Contains($send)) {
        throw "Composer controls overflow their panel: input=$input send=$send composer=$composer"
    }
    foreach ($name in @('WorkspaceSearchButton', 'WindowCloseButton')) {
        if (-not [System.Windows.Rect]::new(0, 0, $Width, $Height).Contains((Get-PreviewBounds $name))) {
            throw "Header control is outside the window: $name"
        }
    }
    $searchBounds = Get-PreviewBounds 'WorkspaceSearchButton'
    $controlsBounds = Get-PreviewBounds 'WorkspaceHeaderControls'
    $searchContent = $visual.FindName('WorkspaceSearchButton').Content
    $contentBounds = $searchContent.TransformToAncestor($visual).TransformBounds([System.Windows.Rect]::new(0, 0, $searchContent.ActualWidth, $searchContent.ActualHeight))
    if ($controlsBounds.Right -gt $searchBounds.Left -or -not $searchBounds.Contains($contentBounds)) {
        throw 'Header project controls or search contents overlap another panel.'
    }
    if ($View -in @('Welcome', 'Reference')) {
        $scene = $welcome.Children[0].Child
        foreach ($card in $scene.Children | Where-Object { $_ -is [System.Windows.Controls.Button] }) {
            $text = $card.Content.Child.Children | Where-Object { $_ -is [System.Windows.Controls.StackPanel] }
            $required = ($text.Children | Measure-Object -Property { $_.DesiredSize.Height } -Sum).Sum
            if ($required -gt $text.ActualHeight + 1) {
                throw "Welcome card text is clipped: $($card.ToolTip); needs $required px, has $($text.ActualHeight) px"
            }
        }
    }
}

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
