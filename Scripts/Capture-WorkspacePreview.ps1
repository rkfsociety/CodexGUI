<#
.SYNOPSIS
Renders a CodexGUI interface view to PNG without launching CodexGUI.

.DESCRIPTION
Loads App.xaml resources and renders Views/WorkspaceView.xaml, the shared empty-chat welcome view,
the built-in Activity and Actions windows, the dark file, folder, or save picker, the automation
editor and its calendar, the worktree setup window, or the Pull Request editor with local preview
data in a standalone STA PowerShell process. It never starts CodexGUI or the App Server.

.EXAMPLE
dotnet build --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1

.EXAMPLE
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -OutputPath .\preview\workspace.png -Width 1600 -Height 1000

.EXAMPLE
dotnet build --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View Welcome -OutputPath .\preview\welcome.png

.EXAMPLE
dotnet build --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View Activity -OutputPath .\preview\activity.png

.EXAMPLE
dotnet build --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View Actions -OutputPath .\preview\actions.png

.EXAMPLE
dotnet build --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View Setup -OutputPath .\preview\setup.png

.EXAMPLE
dotnet build --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View PullRequest -OutputPath .\preview\pull-request.png

.EXAMPLE
dotnet build --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View AutomationEditor -OutputPath .\preview\automation-editor.png

.EXAMPLE
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View Calendar -OutputPath .\preview\calendar.png

.EXAMPLE
dotnet build --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View FilePicker -OutputPath .\preview\file-picker.png

.EXAMPLE
dotnet build --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View FolderPicker -OutputPath .\preview\folder-picker.png

.EXAMPLE
dotnet build --no-restore
pwsh -NoProfile -STA -File Scripts/Capture-WorkspacePreview.ps1 -View SavePicker -OutputPath .\preview\save-picker.png
#>
[CmdletBinding()]
param(
    [ValidateSet('Workspace', 'Welcome', 'Activity', 'Actions', 'Setup', 'PullRequest', 'AutomationEditor', 'Calendar', 'FilePicker', 'FolderPicker', 'SavePicker')]
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

if ($Width -eq 0) { $Width = if ($View -in @('Workspace', 'Welcome')) { 1440 } elseif ($View -eq 'Actions') { 760 } elseif ($View -eq 'Calendar') { 420 } elseif ($View -in @('FilePicker', 'FolderPicker', 'SavePicker')) { 760 } elseif ($View -in @('Setup', 'AutomationEditor')) { 700 } elseif ($View -eq 'PullRequest') { 680 } else { 580 } }
if ($Height -eq 0) { $Height = if ($View -in @('Workspace', 'Welcome')) { 1000 } elseif ($View -eq 'Actions') { 560 } elseif ($View -eq 'Calendar') { 360 } elseif ($View -eq 'Setup') { 470 } elseif ($View -eq 'PullRequest') { 560 } elseif ($View -eq 'AutomationEditor') { 780 } elseif ($View -in @('FilePicker', 'FolderPicker', 'SavePicker')) { 590 } else { 600 } }
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
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:primitives="clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework">
$($resourceNode.InnerXml)
</ResourceDictionary>
"@
$application.Resources.MergedDictionaries.Add([System.Windows.Markup.XamlReader]::Parse($resourcesXaml))

$script:markdownRenderer = $null
$debugAssembly = Join-Path $projectRoot 'bin\Debug\net10.0-windows\CodexGUI.dll'
$releaseAssembly = Join-Path $projectRoot 'bin\Release\net10.0-windows\CodexGUI.dll'
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
        $label.Text = '✳   CODEX'
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
if ($View -in @('Workspace', 'Welcome')) {
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
    if ($View -eq 'Workspace') {
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
            throw 'Build CodexGUI first with dotnet build --no-restore to preview the Welcome screen.'
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
            'С чего начнём?',
            'Опишите задачу простыми словами. Codex изучит проект, объяснит код или поможет внести изменения.',
            [string[]]@('Покажи структуру проекта', 'Найди и исправь ошибку', 'Предложи улучшения кода'),
            $noSuggestionAction))
        [void]$conversation.Children.Add($welcome)
    }
    $visual = $workspace
}
elseif ($View -eq 'Activity') {
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
    $runningEvents.SetValue($activityEventConstructor.Invoke([object[]]@('●  Команда · выполняется', 'dotnet build --no-restore', $false)), 2)
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
        (Join-Path $projectRoot 'bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build --no-restore to preview the Actions window.'
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
    $itemType.GetProperty('Prompt').SetValue($projectAction, 'Проверь документацию проекта и обнови её для текущего изменения.')
    $itemListType.GetMethod('Add').Invoke($projectItems, [object[]]@($projectAction)) | Out-Null
    $save = [Func[bool]]{ $true }
    $saveProject = [Func[bool]]{ $true }
    $insert = [System.Action[string]]{ param([string]$prompt) }
    $constructor = $windowType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 6 } | Select-Object -First 1
    if ($null -eq $constructor) {
        throw 'The Actions window preview constructor was not found in the built assembly.'
    }
    $window = $constructor.Invoke([object[]]@($items, $projectItems, $save, $saveProject, $true, $insert))
    $actionList = $windowType.GetField('_list', $flags).GetValue($window)
    for ($index = 0; $index -lt $actionList.Items.Count; $index++) {
        $actionList.SelectedIndex = $index
        foreach ($buttonName in @('_editButton', '_deleteButton', '_insertButton', '_copyButton')) {
            if (-not $windowType.GetField($buttonName, $flags).GetValue($window).IsEnabled) {
                throw "Selecting action $index did not enable $buttonName."
            }
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
    $testWindow = $constructor.Invoke([object[]]@($testLocalItems, $testProjectItems, $testSave, $testSave, $true, $testInsert))
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
    $itemType.GetProperty('Prompt').SetValue($testProjectAction, 'Verify a project action can be copied to personal.')
    $itemListType.GetMethod('Add').Invoke($reverseProjectItems, [object[]]@($testProjectAction)) | Out-Null
    $reverseWindow = $constructor.Invoke([object[]]@($reverseLocalItems, $reverseProjectItems, $testSave, $testSave, $true, $testInsert))
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
        $reverseLocalItems[0].Prompt -ne $testProjectAction.Prompt) {
        throw 'Copying a project action to the personal library did not preserve both independent entries.'
    }

    $filterLocalItems = [Activator]::CreateInstance($itemListType)
    $filterProjectItems = [Activator]::CreateInstance($itemListType)
    $filteredAction = [Activator]::CreateInstance($itemType)
    $itemType.GetProperty('Name').SetValue($filteredAction, 'Existing action')
    $itemType.GetProperty('Prompt').SetValue($filteredAction, 'Searchable phrase')
    $itemListType.GetMethod('Add').Invoke($filterLocalItems, [object[]]@($filteredAction)) | Out-Null
    $filterWindow = $constructor.Invoke([object[]]@($filterLocalItems, $filterProjectItems, $testSave, $testSave, $true, $testInsert))
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
elseif ($View -eq 'PullRequest') {
    $assemblyCandidates = @(
        (Join-Path $projectRoot 'bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build --no-restore to preview the Pull Request editor.'
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
        (Join-Path $projectRoot 'bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build --no-restore to preview the automation editor.'
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
        (Join-Path $projectRoot 'bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build --no-restore to preview the file picker.'
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
        (Join-Path $projectRoot 'bin\Debug\net10.0-windows\CodexGUI.dll'),
        (Join-Path $projectRoot 'bin\Release\net10.0-windows\CodexGUI.dll')
    ) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTime -Descending
    $assemblyPath = $assemblyCandidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) {
        throw 'Build CodexGUI first with dotnet build --no-restore to preview the worktree setup window.'
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
