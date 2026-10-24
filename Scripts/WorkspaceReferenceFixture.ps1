# Preview-only data from the supplied design. This script never loads user state or starts App Server.
function Set-WorkspaceReferenceFixture {
    param([System.Windows.Controls.Grid]$Workspace, [System.Reflection.Assembly]$Assembly)

    $flags = [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic
    $headerMethod = $Assembly.GetType('CodexGUI.MainWindow').GetMethod('WorkspaceFileHeader', $flags)
    function New-ReferenceTreeNode([string]$Label, [bool]$Folder, [bool]$Expanded = $false) {
        $node = [System.Windows.Controls.TreeViewItem]::new()
        $node.Header = $headerMethod.Invoke($null, [object[]]@([string]$Label, $Folder))
        $node.IsExpanded = $Expanded
        return $node
    }
    $tree = $Workspace.FindName('FileTree')
    $tree.Items.Clear()
    $root = New-ReferenceTreeNode 'CodexGUI' $true $true
    $hint = [System.Windows.Controls.TextBlock]::new()
    $hint.Text = '~/Projects/CodexGUI'; $hint.FontSize = 11
    $hint.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#6F8797')
    $hint.Margin = [System.Windows.Thickness]::new(12, 0, 0, 0)
    [void]$root.Header.Children.Add($hint)
    $src = New-ReferenceTreeNode 'src' $true $true
    [void]$src.Items.Add((New-ReferenceTreeNode 'main' $true))
    $renderer = New-ReferenceTreeNode 'renderers' $true $true
    $components = New-ReferenceTreeNode 'components' $true $true
    foreach ($file in @('ChatView.tsx', 'ProjectGraph.tsx', 'AgentPanel.tsx', 'CommandDock.tsx')) {
        $item = New-ReferenceTreeNode $file $false
        [void]$components.Items.Add($item)
        if ($file -eq 'ChatView.tsx') { $item.IsSelected = $true }
    }
    [void]$renderer.Items.Add($components)
    foreach ($folder in @('services', 'types', 'utils')) {
        $item = New-ReferenceTreeNode $folder $true
        [void]$item.Items.Add((New-ReferenceTreeNode 'preview.ts' $false))
        [void]$renderer.Items.Add($item)
    }
    [void]$src.Items.Add($renderer)
    [void]$root.Items.Add($src)
    foreach ($folder in @('assets', 'docs')) {
        $item = New-ReferenceTreeNode $folder $true
        [void]$item.Items.Add((New-ReferenceTreeNode 'preview' $false))
        [void]$root.Items.Add($item)
    }
    [void]$tree.Items.Add($root)

    $Workspace.FindName('HeaderStatus').Text = 'Агент готов'
    $Workspace.FindName('ThreadGoalButton').IsEnabled = $true
    $Workspace.FindName('ThreadGoalLabel').Text = 'Реализовать новый компонент чата'
    $Workspace.FindName('WorkspaceGoalDetail').Text = "Добавить потоковый вывод, поддержку`nвложений и улучшить UX."
    $Workspace.FindName('GoalStepCount').Text = '2/4'
    $Workspace.FindName('GoalProgressArc').Data = [System.Windows.Media.Geometry]::Parse('M24,4 A20,20 0 0 1 24,44')
    $steps = $Workspace.FindName('GoalSteps'); $steps.Children.Clear()
    $labels = @('Проанализировать текущую реализацию', 'Спроектировать изменения', 'Реализовать компонент', 'Протестировать и задокументировать')
    for ($i = 0; $i -lt $labels.Count; $i++) {
        $check = [System.Windows.Controls.CheckBox]::new()
        $check.Style = $Workspace.FindResource('GoalStepCheckBox')
        $check.Content = $labels[$i]; $check.FontSize = 13; $check.IsChecked = $i -lt 2
        $check.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#ADC0CD')
        $check.Margin = [System.Windows.Thickness]::new(0, 5, 0, 6)
        [void]$steps.Children.Add($check)
    }
    $context = $Workspace.FindName('WorkspaceContextList'); $context.Children.Clear()
    $titles = @('Обсуждали архитектуру чата', 'Реализовывали потоковый вывод', 'Добавляли поддержку markdown', 'Исправляли ошибку в типах')
    $times = @('2 часа назад', 'Вчера', '3 дня назад', '1 неделю назад')
    for ($i = 0; $i -lt $titles.Count; $i++) {
        $row = [System.Windows.Controls.Grid]::new(); $row.Height = 33
        foreach ($width in @('28', '*', 'Auto')) {
            $column = [System.Windows.Controls.ColumnDefinition]::new()
            $column.Width = [System.Windows.GridLengthConverter]::new().ConvertFromString($width)
            [void]$row.ColumnDefinitions.Add($column)
        }
        $icon = [System.Windows.Shapes.Path]::new()
        $icon.Data = [System.Windows.Media.Geometry]::Parse('M4,2 H13 L19,8 V22 H4 Z M13,2 V8 H19 M8,13 H15')
        $icon.Width = 14; $icon.Height = 18; $icon.Stretch = 'Uniform'; $icon.StrokeThickness = 1.4
        $icon.Stroke = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#8DA6B5')
        $icon.HorizontalAlignment = 'Left'; $icon.VerticalAlignment = 'Center'; [void]$row.Children.Add($icon)
        $label = [System.Windows.Controls.TextBlock]::new(); $label.Text = $titles[$i]; $label.FontSize = 12
        $label.TextTrimming = 'CharacterEllipsis'; $label.VerticalAlignment = 'Center'
        [System.Windows.Controls.Grid]::SetColumn($label, 1); [void]$row.Children.Add($label)
        $date = [System.Windows.Controls.TextBlock]::new(); $date.Text = $times[$i]; $date.FontSize = 11.8
        $date.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#8198A7')
        $date.VerticalAlignment = 'Center'; $date.Margin = [System.Windows.Thickness]::new(8, 0, 0, 0)
        [System.Windows.Controls.Grid]::SetColumn($date, 2); [void]$row.Children.Add($date)
        [void]$context.Children.Add($row)
    }
    $Workspace.FindName('AccessModeLabel').Text = 'Полный доступ'
    $Workspace.FindName('AccessModeLabel').Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#F0A277')
    $nodeType = $Assembly.GetType('CodexGUI.WorkspaceMapNode')
    $constructor = $nodeType.GetConstructor([Type[]]@([string], [string]))
    $names = @('components', 'services', 'types', 'assets', 'utils', 'src')
    $nodes = [Array]::CreateInstance($nodeType, $names.Count)
    for ($i = 0; $i -lt $names.Count; $i++) {
        $nodes.SetValue($constructor.Invoke([object[]]@([string]$names[$i], [string]("C:\Projects\CodexGUI\" + $names[$i]))), $i)
    }
    $select = [System.Action[string]]{ param([string]$path) }
    [void]$Assembly.GetType('CodexGUI.WorkspaceMapView').GetMethod('Render', $flags).Invoke($null,
        [object[]]@($Workspace.FindName('ProjectMap'), $nodes, 'C:\Projects\CodexGUI', 1.0, $select))
}
