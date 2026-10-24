using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.IO.Path;

namespace CodexGUI;

public partial class MainWindow
{
    private string WorkspaceDirectory => _currentThread?.WorktreePath ?? _projectPath;
    private int _workspaceMapMode;
    private double _workspaceMapZoom = 1;

    private void BindWorkspaceEvents()
    {
        Click("WindowMinimizeButton", (_, _) => WindowState = WindowState.Minimized);
        Click("WindowMaximizeButton", (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        Click("WindowCloseButton", (_, _) => Close());
        Click("WorkspaceProjectButton", (_, _) => ShowProjectMenu());
        Click("WorkspaceFolderButton", (_, _) => OpenProjectFolder());
        Click("WorkspaceBranchButton", (_, _) => ShowWorkspaceGit());
        Click("WorkspaceSearchButton", (_, _) => ShowWorkspaceSearch());
        Click("QuickFindButton", (_, _) => ShowWorkspaceSearch());
        Click("QuickFileButton", async (_, _) => await PickWorkspaceFileAsync());
        Click("QuickTerminalButton", (_, _) => ShowTerminalWindow());
        Click("QuickDiffButton", (_, _) => ShowWorkspaceGit());
        Click("QuickCreateButton", (_, _) => CreateWorkspaceFile());
        Click("QuickExplainButton", (_, _) =>
        {
            AddFileContext();
            SetWorkspaceDraft("Объясни код в прикреплённом файле.");
        });
        Click("AddGoalStepButton", (_, _) => AddWorkspaceGoalStep());
        Click("WorkspaceAttachButton", (_, _) => AddFileContext());
        Click("WorkspaceContextButton", async (_, _) => await InsertRepositoryContextAsync());
        Click("WorkspaceWebButton", (_, _) => ShowWorkspaceWebSearch());
        Click("WorkspaceChatsButton", (_, _) => ShowSearch());
        Click("WorkspaceAgentButton", async (_, _) => await ShowSubagentsAsync());
        Click("WorkspaceToolsButton", (_, _) => ShowWorkspaceTools());
        Click("MapFilesButton", (_, _) => { _workspaceMapMode = 0; RenderWorkspaceMap(); });
        Click("MapDependenciesButton", (_, _) => { _workspaceMapMode = 1; RenderWorkspaceMap(); });
        Click("MapLayersButton", (_, _) => { _workspaceMapMode = 2; RenderWorkspaceMap(); });
        Click("MapZoomInButton", (_, _) => { _workspaceMapZoom = Math.Min(2, _workspaceMapZoom + 0.2); RenderWorkspaceMap(); });
        Click("MapZoomOutButton", (_, _) => { _workspaceMapZoom = Math.Max(0.6, _workspaceMapZoom - 0.2); RenderWorkspaceMap(); });
        Click("MapResetButton", (_, _) => { _workspaceMapZoom = 1; RenderWorkspaceMap(); });
        Click("MapFullButton", (_, _) => ShowFullWorkspaceMap());
    }

    private void ShowWorkspaceGit()
    {
        ToggleContext(true);
        if (Control<Border>("RepositoryCard") is { } card) card.Visibility = Visibility.Visible;
        if (Control<Expander>("RepositoryDetails") is { } details)
        {
            details.IsExpanded = true;
            details.BringIntoView();
        }
    }

    private void ShowWorkspaceTools()
    {
        if (Control<Button>("WorkspaceToolsButton") is not { } button) return;
        var menu = new ContextMenu();
        Add("Новый чат", NewChat);
        Add("Проекты", ShowProjectMenu);
        Add("История чатов", ShowSearch);
        Add("Активность", ShowActivityWindow);
        Add("Расписания", async () => await ShowAutomationsAsync());
        Add("Плагины", () => ShowExtensions(showSkills: false));
        Add("Скиллы", () => ShowExtensions(showSkills: true));
        Add("MCP-серверы", ShowMcpServers);
        Add("Действия", ShowActionsWindow);
        Add("Терминал", ShowTerminalWindow);
        Add("Усилия · " + EffortDisplayName(_selectedEffort), async () => await ShowEffortMenuAsync());
        Add(_currentThread?.UseWorktree == true ? "Рабочая копия · изолированная" : "Рабочая копия · локальная", async () => await ToggleWorktreeModeAsync());
        Add("Лимиты аккаунта", () => CodexMessageDialog.Show(this, _usageLimitsText + "\n" + _usageLimitsTooltip, "Лимиты аккаунта", MessageBoxButton.OK, MessageBoxImage.Information));
        Add("Показать / скрыть проект", ToggleSidebar);
        Add("Показать / скрыть контекст", () => ToggleContext());
        menu.PlacementTarget = button; menu.IsOpen = true;
        void Add(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
    }

    private void ShowWorkspaceWebSearch()
    {
        var window = new Window { Owner = this, Title = "Веб-поиск", Width = 540, Height = 190,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        CodexWindowTheme.Apply(window);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Открыть результаты в браузере", Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { MinHeight = 30 };
        var search = new Button { Content = "Найти", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        search.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.bing.com/search?q=" + Uri.EscapeDataString(input.Text.Trim())) { UseShellExecute = true });
                window.Close();
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                CodexMessageDialog.Show(window, error.Message, "Не удалось открыть браузер", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        panel.Children.Add(input); panel.Children.Add(search); window.Content = panel;
        window.Loaded += (_, _) => input.Focus(); window.ShowDialog();
    }

    private void SetWorkspaceDraft(string text)
    {
        if (Control<TextBox>("PromptInput") is not { } input) return;
        input.Focus();
        input.Text = input.Text == PromptPlaceholder || string.IsNullOrWhiteSpace(input.Text)
            ? text : input.Text + Environment.NewLine + text;
        input.CaretIndex = input.Text.Length;
    }

    private void UpdateWorkspacePanels()
    {
        if (Control<TextBlock>("WorkspaceProjectName") is { } project)
            project.Text = _selectedProject?.Name ?? Path.GetFileName(WorkspaceDirectory);
        if (Control<StackPanel>("WorkspaceContextList") is { } context)
        {
            context.Children.Clear();
            foreach (var name in new[] { "AGENTS.md", ".codex/memory.md", ".codex/decisions.md", "README.md" })
            {
                var path = Path.Combine(WorkspaceDirectory, name);
                if (!File.Exists(path)) continue;
                var button = new Button { Content = "▤  " + name, FontSize = 11,
                    HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(3, 7, 3, 7), ToolTip = path };
                button.Click += async (_, _) => await PreviewWorkspaceFileAsync(path);
                context.Children.Add(button);
            }
            if (context.Children.Count == 0)
                context.Children.Add(new TextBlock { Text = "В проекте пока нет файлов контекста.", FontSize = 11,
                    Foreground = Brush("#91A8B5"), TextWrapping = TextWrapping.Wrap });
        }
        RenderWorkspaceGoalSteps();
    }

    private TreeViewItem CreateWorkspaceDirectory(string path)
    {
        var item = new TreeViewItem { Header = WorkspaceFileHeader(path, true), Tag = path,
            Foreground = Brush("#B6D4DE"), ToolTip = path };
        item.Items.Add(new TreeViewItem { Header = "Загрузка…" });
        item.Expanded += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, item) || item.Items.Count != 1
                || item.Items[0] is not TreeViewItem { Tag: null }) return;
            PopulateWorkspaceDirectory(item, path);
        };
        return item;
    }

    private void PopulateWorkspaceDirectory(TreeViewItem parent, string path)
    {
        parent.Items.Clear();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(path)
                         .Where(IsWorkspaceDirectoryVisible).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(150))
                parent.Items.Add(CreateWorkspaceDirectory(directory));
            foreach (var file in Directory.EnumerateFiles(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(300))
            {
                var item = new TreeViewItem { Header = WorkspaceFileHeader(file, false), Tag = file, ToolTip = "Двойной щелчок: просмотр файла" };
                item.MouseDoubleClick += async (_, args) =>
                {
                    args.Handled = true;
                    await PreviewWorkspaceFileAsync(file);
                };
                parent.Items.Add(item);
            }
            if (parent.Items.Count == 0) parent.Items.Add(new TreeViewItem { Header = "Папка пуста", IsEnabled = false });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            parent.Items.Add(new TreeViewItem { Header = "Не удалось прочитать папку", ToolTip = error.Message, IsEnabled = false });
        }
    }

    private static FrameworkElement WorkspaceFileHeader(string path, bool directory)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(new System.Windows.Shapes.Path
        {
            Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0),
            Stroke = Brush(directory ? "#70E8CC" : "#B6CCD8"), StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse(directory ? "M1,4 L1,14 L17,14 L17,4 L9,4 L7,1 L1,1 Z" : "M3,1 L10,1 L15,6 L15,17 L3,17 Z M10,1 L10,6 L15,6 M6,10 L12,10 M6,13 L11,13")
        });
        panel.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 13 });
        return panel;
    }

    private static bool IsWorkspaceDirectoryVisible(string path) =>
        !new[] { ".git", "bin", "obj", "node_modules", ".vs" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private void RenderWorkspaceMap(Canvas? target = null)
    {
        var canvas = target ?? Control<Canvas>("ProjectMap");
        if (canvas is null) return;
        foreach (var (name, mode) in new[] { ("MapFilesButton", 0), ("MapDependenciesButton", 1), ("MapLayersButton", 2) })
            if (Control<Button>(name) is { } tab) tab.Background = Brush(_workspaceMapMode == mode ? "#163B3A" : "#0B151A");
        try
        {
            var nodes = WorkspaceProjectMap.Read(WorkspaceDirectory, _workspaceMapMode);
            WorkspaceMapView.Render(canvas, nodes, WorkspaceDirectory, _workspaceMapZoom, async path =>
            {
                if (File.Exists(path)) { await PreviewWorkspaceFileAsync(path); return; }
                if (target is not null)
                {
                    var file = CodexFilePickerWindow.OpenFile(this, "Открыть файл", path, []);
                    if (file is not null) await PreviewWorkspaceFileAsync(file);
                    return;
                }
                if (Control<TreeView>("FileTree") is not { } tree) return;
                foreach (var root in tree.Items.OfType<TreeViewItem>())
                {
                    root.IsExpanded = true;
                    var item = string.Equals(root.Tag as string, path, StringComparison.OrdinalIgnoreCase)
                        ? root : root.Items.OfType<TreeViewItem>().FirstOrDefault(child => string.Equals(child.Tag as string, path, StringComparison.OrdinalIgnoreCase));
                    if (item is null) continue;
                    item.IsExpanded = true; item.IsSelected = true; item.BringIntoView(); item.Focus();
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            canvas.Children.Clear();
            canvas.Children.Add(new TextBlock { Text = "Карта недоступна", ToolTip = error.Message });
        }
    }

    private void ShowFullWorkspaceMap()
    {
        var window = new Window { Owner = this, Title = "Карта проекта", Width = 900, Height = 650,
            MinWidth = 400, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        CodexWindowTheme.Apply(window);
        var canvas = new Canvas { Width = 300, Height = 230 };
        RenderWorkspaceMap(canvas);
        window.Content = new Viewbox { Child = canvas, Margin = new Thickness(30) };
        window.ShowDialog();
    }

    private async Task PickWorkspaceFileAsync()
    {
        var path = CodexFilePickerWindow.OpenFile(this, "Открыть файл проекта", WorkspaceDirectory, []);
        if (path is not null) await PreviewWorkspaceFileAsync(path);
    }

    private async Task PreviewWorkspaceFileAsync(string path, int line = 1)
    {
        try
        {
            if (new FileInfo(path).Length > 2 * 1024 * 1024)
                throw new IOException("Для просмотра выберите текстовый файл размером до 2 МБ.");
            var text = await File.ReadAllTextAsync(path);
            if (text.Contains('\0')) throw new IOException("Этот файл не является текстовым.");
            var window = new Window { Owner = this, Title = Path.GetFileName(path), Width = 900, Height = 650,
                MinWidth = 500, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            CodexWindowTheme.Apply(window);
            var panel = new DockPanel { Margin = new Thickness(16) };
            var attach = new Button { Content = "Прикрепить к запросу", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            attach.Click += (_, _) => { AddComposerAttachments([path]); window.Close(); };
            DockPanel.SetDock(attach, Dock.Bottom); panel.Children.Add(attach);
            var input = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"),
                FontSize = 13, Background = Brush("#0A141A"), Foreground = Brush("#D7E7EB"),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            input.Loaded += (_, _) => input.ScrollToLine(Math.Clamp(line - 1, 0, Math.Max(0, input.LineCount - 1)));
            panel.Children.Add(input); window.Content = panel; window.ShowDialog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CodexMessageDialog.Show(this, error.Message, "Просмотр файла", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CreateWorkspaceFile()
    {
        var path = CodexFilePickerWindow.SaveFile(this, "Создать файл", WorkspaceDirectory, "new-file.txt", ".txt");
        if (path is null) return;
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            RefreshFileTree();
            AddComposerAttachments([path]);
            SetStatus("Файл создан", path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CodexMessageDialog.Show(this, error.Message, "Создание файла", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowWorkspaceSearch()
    {
        var window = new WorkspaceSearchWindow(WorkspaceDirectory, (path, line) => PreviewWorkspaceFileAsync(path, line)) { Owner = this };
        window.ShowDialog();
    }

    private void RenderWorkspaceGoalSteps()
    {
        if (Control<StackPanel>("GoalSteps") is not { } panel) return;
        panel.Children.Clear();
        var thread = _currentThread;
        if (Control<TextBlock>("GoalStepCount") is { } count)
            count.Text = thread is null ? "0/0" : $"{thread.GoalSteps.Count(step => step.IsComplete)}/{thread.GoalSteps.Count}";
        if (Control<Button>("AddGoalStepButton") is { } add) add.IsEnabled = _selectedProject is not null;
        var completed = thread?.GoalSteps.Count(step => step.IsComplete) ?? 0;
        var fraction = thread is null || thread.GoalSteps.Count == 0 ? 0 : completed / (double)thread.GoalSteps.Count;
        if (Control<System.Windows.Shapes.Path>("GoalProgressArc") is { } arc)
        {
            if (fraction == 0) arc.Data = Geometry.Empty;
            else if (fraction == 1) arc.Data = new EllipseGeometry(new Point(24, 24), 20, 20);
            else
            {
                var end = new Point(24 + 20 * Math.Sin(fraction * Math.PI * 2), 24 - 20 * Math.Cos(fraction * Math.PI * 2));
                var figure = new PathFigure { StartPoint = new Point(24, 4), IsClosed = false, IsFilled = false };
                figure.Segments.Add(new ArcSegment(end, new Size(20, 20), 0, fraction > 0.5, SweepDirection.Clockwise, true));
                arc.Data = new PathGeometry([figure]);
            }
        }
        if (thread is null) return;
        foreach (var step in thread.GoalSteps)
        {
            var row = new DockPanel();
            var remove = new Button { Content = "×", Padding = new Thickness(4), ToolTip = "Удалить шаг" };
            remove.Click += async (_, _) =>
            {
                panel.IsEnabled = false;
                try
                {
                    var index = thread.GoalSteps.IndexOf(step);
                    thread.GoalSteps.Remove(step);
                    if (!await SaveStateAsync()) thread.GoalSteps.Insert(index, step);
                }
                finally { panel.IsEnabled = true; RenderWorkspaceGoalSteps(); }
            };
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var check = new CheckBox { Content = new TextBlock { Text = step.Text, TextWrapping = TextWrapping.Wrap, FontSize = 11 },
                IsChecked = step.IsComplete, Margin = new Thickness(0, 5, 0, 5) };
            check.Click += async (_, _) =>
            {
                panel.IsEnabled = false;
                try
                {
                    var previous = step.IsComplete;
                    step.IsComplete = check.IsChecked == true;
                    if (!await SaveStateAsync()) step.IsComplete = previous;
                }
                finally { panel.IsEnabled = true; RenderWorkspaceGoalSteps(); }
            };
            row.Children.Add(check); panel.Children.Add(row);
        }
    }

    private void AddWorkspaceGoalStep()
    {
        if (_currentThread is null && _selectedProject is not null) NewChat();
        var thread = _currentThread;
        if (thread is null) return;
        var window = new Window { Owner = this, Title = "Новый шаг цели", Width = 460, Height = 180,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        CodexWindowTheme.Apply(window);
        var panel = new StackPanel { Margin = new Thickness(18) };
        var input = new TextBox { Margin = new Thickness(0, 0, 0, 12), MaxLength = 300 };
        var add = new Button { Content = "Добавить", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        add.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            add.IsEnabled = false;
            var step = new WorkspaceGoalStep { Text = input.Text.Trim() };
            thread.GoalSteps.Add(step);
            if (!await SaveStateAsync()) { thread.GoalSteps.Remove(step); add.IsEnabled = true; return; }
            RenderWorkspaceGoalSteps(); window.Close();
        };
        panel.Children.Add(input); panel.Children.Add(add); window.Content = panel;
        window.Loaded += (_, _) => input.Focus(); window.ShowDialog();
    }

    private sealed class WorkspaceGoalStep
    {
        public string Text { get; set; } = "";
        public bool IsComplete { get; set; }
    }
}
