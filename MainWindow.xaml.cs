using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Xml;
using Path = System.IO.Path;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using DrawingIcon = System.Drawing.Icon;
using DrawingColor = System.Drawing.Color;

namespace CodexGUI;

public partial class MainWindow : Window
{
    private const string PromptPlaceholder = "Спросите Codex о задаче или попросите изменить проект…";
    private const string DefaultModel = "gpt-6-luna";
    private const string DefaultEffort = "high";
    private readonly ObservableCollection<ChatThread> _threads = [];
    private readonly List<ProjectEntry> _projects = [];
    private IReadOnlyList<ModelOption> _availableModels = [];
    private Task<IReadOnlyList<ModelOption>>? _modelCatalogTask;
    private readonly Dictionary<string, FrameworkElement> _named = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _reloadDebounce;
    private readonly DispatcherTimer _stateSaveDebounce;
    private readonly DispatcherTimer _usageRefreshTimer;
    private bool _isRefreshingUsage;
    private string _usageLimitsText = "Загружаю лимиты Codex…";
    private string _usageLimitsTooltip = "Лимиты аккаунта Codex";
    private FileSystemWatcher? _viewWatcher;
    private Forms.NotifyIcon? _trayIcon;
    private DrawingIcon? _trayImage;
    private Process? _activeProcess;
    private bool _isRunning;
    private bool _sidebarCollapsed;
    private bool _contextOpen;
    private bool _showArchivedChats;
    private string _projectPath = FindProjectRoot();
    private ProjectEntry? _selectedProject;
    private string? _startupIssue;
    private string _selectedModel = DefaultModel;
    private string _selectedEffort = DefaultEffort;
    private ChatThread? _currentThread;
    private Grid? _activeView;
    private TextBlock? _streamingAssistantTextBlock;

    public MainWindow()
    {
        InitializeComponent();
        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce.Stop();
            ReloadView();
        };
        _stateSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _stateSaveDebounce.Tick += (_, _) =>
        {
            _stateSaveDebounce.Stop();
            SaveState();
        };
        _usageRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _usageRefreshTimer.Tick += async (_, _) => await RefreshUsageLimitsAsync();
        LoadState();
        ReloadView();
        InitializeTrayIcon();
        StartViewWatcher();
        _usageRefreshTimer.Start();
        Loaded += (_, _) =>
        {
            _ = RefreshUsageLimitsAsync();
            if (_currentThread is not null) _ = RestoreThreadHistoryAsync(_currentThread);
        };
        Closed += (_, _) =>
        {
            _usageRefreshTimer.Stop();
            if (_stateSaveDebounce.IsEnabled)
            {
                _stateSaveDebounce.Stop();
                SaveState();
            }
            StopCodexProcess();
        };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) Hide();
        };
    }

    private void InitializeTrayIcon()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить файл приложения.");
        _trayImage = DrawingIcon.ExtractAssociatedIcon(executable)
            ?? throw new InvalidOperationException("В приложении не найдена иконка для трея.");

        var menu = new Forms.ContextMenuStrip
        {
            BackColor = DrawingColor.FromArgb(30, 32, 36),
            ForeColor = DrawingColor.FromArgb(240, 240, 241),
            ShowImageMargin = false
        };
        var showItem = new Forms.ToolStripMenuItem("Открыть Codex");
        showItem.Click += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        var newChatItem = new Forms.ToolStripMenuItem("Новый чат");
        newChatItem.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ShowFromTray();
            NewChat();
        });
        var exitItem = new Forms.ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(Close);
        menu.Items.Add(showItem);
        menu.Items.Add(newChatItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayImage,
            Text = "Codex — готов к работе",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(ShowFromTray);
        };
    }

    private void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ApplyDarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        uint captionColor = 0x00111310;
        uint textColor = 0x00F1F0F0;
        _ = DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(uint));
        _ = DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(uint));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int valueSize);

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexGUI.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private string ViewPath => Path.Combine(FindProjectRoot(), "Views", "WorkspaceView.xaml");

    private void StartViewWatcher()
    {
        var path = ViewPath;
        var folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder)) return;

        _viewWatcher = new FileSystemWatcher(folder, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _viewWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        });
        _viewWatcher.Created += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        });
        _viewWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        });
    }

    private void ReloadView()
    {
        try
        {
            var xaml = File.ReadAllText(ViewPath);
            using var reader = XmlReader.Create(new StringReader(xaml));
            if (XamlReader.Load(reader) is not Grid root)
                throw new InvalidDataException("WorkspaceView.xaml must have a Grid root element.");

            _named.Clear();
            IndexNamedElements(root);
            _activeView = root;
            ViewHost.Content = root;
            BindEvents();
            RefreshView();
        }
        catch (Exception error)
        {
            SetStatus("Ошибка интерфейса", error.Message, isError: true);
        }
    }

    private void IndexNamedElements(DependencyObject parent, HashSet<DependencyObject>? visited = null)
    {
        visited ??= new HashSet<DependencyObject>();
        if (!visited.Add(parent)) return;
        if (parent is FrameworkElement element && !string.IsNullOrEmpty(element.Name))
            _named[element.Name] = element;

        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            IndexNamedElements(child, visited);

        if (parent is Visual or Visual3D)
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                IndexNamedElements(VisualTreeHelper.GetChild(parent, index), visited);
    }

    private T? Control<T>(string name) where T : class => _named.GetValueOrDefault(name) as T;

    private void BindEvents()
    {
        Click("NewChatButton", (_, _) => NewChat());
        Click("SearchButton", (_, _) => ShowSearch());
        Click("CollapseButton", (_, _) => ToggleSidebar());
        Click("ContextButton", (_, _) => ToggleContext());
        Click("CloseContextButton", (_, _) => ToggleContext(false));
        Click("OpenFolderButton", (_, _) => OpenProjectFolder());
        Click("ProjectButton", (_, _) => ChooseProjectFolder());
        Click("AddProjectButton", (_, _) => ShowProjectMenu());
        Click("ArchivedChatsButton", (_, _) => ToggleArchivedChats());
        Click("SendButton", async (_, _) => await SendPromptAsync());
        Click("ModeButton", (_, _) => ShowAccessMenu());
        Click("ModelButton", async (_, _) => await ShowModelMenuAsync());
        Click("EffortButton", async (_, _) => await ShowEffortMenuAsync());
        Click("AddContextButton", (_, _) => AddFileContext());
        Click("ScreenshotButton", async (_, _) => await CaptureScreenshotAsync());

        if (Control<TextBox>("PromptInput") is { } prompt)
        {
            prompt.GotKeyboardFocus += (_, _) =>
            {
                if (prompt.Text == PromptPlaceholder) prompt.Clear();
            };
            prompt.LostKeyboardFocus += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(prompt.Text)) prompt.Text = PromptPlaceholder;
            };
            prompt.PreviewKeyDown += PromptInput_PreviewKeyDown;
        }
    }

    private void Click(string name, RoutedEventHandler handler)
    {
        if (Control<Button>(name) is { } button) button.Click += handler;
    }

    private void RefreshView()
    {
        if (Control<TextBlock>("ProjectPathShort") is { } shortPath)
            shortPath.Text = Path.GetFileName(_projectPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : _projectPath;
        if (Control<TextBlock>("FolderLabel") is { } folderLabel) folderLabel.Text = _projectPath;
        if (Control<TextBlock>("HeaderTitle") is { } title) title.Text = _currentThread?.Title ?? "Новая задача";
        if (Control<TextBlock>("AccessModeLabel") is { } accessModeLabel)
        {
            accessModeLabel.Text = IsFullAccess ? "Полный доступ" : "Попросить подтверждение";
            accessModeLabel.Foreground = Brush(IsFullAccess ? "#F0A277" : "#C4C5C9");
        }
        UpdateComposerSettingsLabels();
        UpdateChatSectionHeading();
        if (Control<TextBlock>("UsageLimitsLabel") is { } usageLabel)
        {
            usageLabel.Text = _usageLimitsText;
            usageLabel.ToolTip = _usageLimitsTooltip;
        }
        UpdateContextIndicator();
        if (_activeView is { ColumnDefinitions.Count: 3 } view)
        {
            view.ColumnDefinitions[0].Width = _sidebarCollapsed ? new GridLength(0) : new GridLength(250);
            view.ColumnDefinitions[2].Width = _contextOpen ? new GridLength(310) : new GridLength(0);
        }
        if (Control<Border>("ContextPanel") is { } contextPanel)
            contextPanel.Visibility = _contextOpen ? Visibility.Visible : Visibility.Collapsed;

        RenderThreads();
        RenderProjects();
        RenderConversation();
        if (_contextOpen) RefreshFileTree();
        SetStatus(_isRunning ? "Codex работает…" : "Codex CLI готов",
            _startupIssue ?? (_isRunning ? "Запрос выполняется в выбранной папке." : "Запросы выполняются в выбранной папке."),
            isError: _startupIssue is not null);
    }

    private void RenderThreads()
    {
        if (Control<StackPanel>("ChatList") is not { } list) return;
        list.Children.Clear();
        foreach (var thread in _threads.Where(thread =>
                     thread.ProjectId == _selectedProject?.Id && thread.IsArchived == _showArchivedChats).Reverse())
        {
            var button = new Button
            {
                Content = thread.Title,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8, 7, 8),
                Margin = new Thickness(0, 1, 0, 1),
                Background = thread == _currentThread ? Brush("#27292E") : Brushes.Transparent,
                Foreground = Brush(thread == _currentThread ? "#F0F0F1" : "#A4A5AA"),
                FontSize = 12,
                ToolTip = thread.Title
            };
            button.Click += (_, _) => SelectThread(thread);
            button.PreviewMouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowThreadContextMenu(button, thread);
            };
            list.Children.Add(button);
        }
    }

    private void ShowThreadContextMenu(Button target, ChatThread thread)
    {
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            AllowsTransparency = true,
            HorizontalOffset = 2,
            VerticalOffset = 4
        };
        var actions = new StackPanel();
        var archive = CreateThreadActionButton(
            thread.IsArchived ? "Вернуть из архива" : "Архивировать",
            "M3,5 H17 M5,5 V17 H15 V5 M7,5 V3 H13 V5 M7,8 V14 M11,8 V14",
            "#D5D6D9");
        archive.IsEnabled = !_isRunning;
        archive.Click += async (_, _) =>
        {
            popup.IsOpen = false;
            await SetThreadArchivedAsync(thread, !thread.IsArchived);
        };
        var delete = CreateThreadActionButton("Удалить чат", "M4,6 H16 M6,6 V17 H14 V6 M7,6 V4 H13 V6 M8,9 V14 M12,9 V14", "#F08C8C");
        delete.IsEnabled = !_isRunning;
        delete.Click += async (_, _) =>
        {
            popup.IsOpen = false;
            await DeleteThreadAsync(thread);
        };
        actions.Children.Add(archive);
        actions.Children.Add(new Border { Height = 1, Background = Brush("#34363B"), Margin = new Thickness(7, 4, 7, 4) });
        actions.Children.Add(delete);

        popup.Child = new Border
        {
            Width = 194,
            Background = Brush("#202124"),
            BorderBrush = Brush("#3B3D42"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(5),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 4, Opacity = 0.38 },
            Child = actions
        };
        popup.IsOpen = true;
    }

    private Button CreateThreadActionButton(string title, string iconData, string color) => new()
    {
        Style = Control<Button>("SendButton")?.Style,
        Width = 182,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        Padding = new Thickness(9, 7, 9, 7),
        Background = Brushes.Transparent,
        Foreground = Brush(color),
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Width = 160,
            Children =
            {
                new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(iconData),
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(0, 0, 10, 0),
                    Stretch = Stretch.Uniform,
                    Stroke = Brush(color),
                    StrokeThickness = 1.25,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                },
                new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
            }
        }
    };

    private void UpdateChatSectionHeading()
    {
        if (Control<TextBlock>("ChatSectionLabel") is { } heading)
            heading.Text = _showArchivedChats ? "АРХИВ" : "ЧАТЫ";
        if (Control<Button>("ArchivedChatsButton") is { } button)
        {
            var count = _threads.Count(thread => thread.ProjectId == _selectedProject?.Id && thread.IsArchived);
            button.Content = _showArchivedChats ? "Все чаты" : count > 0 ? $"Архив · {count}" : "Архив";
            button.ToolTip = _showArchivedChats ? "Вернуться к активным чатам" : "Открыть архив чатов";
        }
    }

    private void ToggleArchivedChats()
    {
        _showArchivedChats = !_showArchivedChats;
        _currentThread = _threads.LastOrDefault(thread =>
            thread.ProjectId == _selectedProject?.Id && thread.IsArchived == _showArchivedChats);
        UpdateChatSectionHeading();
        RefreshChatSelection();
    }

    private async Task SetThreadArchivedAsync(ChatThread thread, bool archived)
    {
        if (_isRunning) return;
        try
        {
            if (thread.CodexSessionId is { Length: > 0 } sessionId)
                await RunSessionCommandAsync(archived ? "archive" : "unarchive", sessionId);
            thread.IsArchived = archived;
            if (archived && _currentThread == thread)
                _currentThread = _threads.LastOrDefault(item => item != thread && item.ProjectId == thread.ProjectId && !item.IsArchived);
            if (!archived)
            {
                _showArchivedChats = false;
                _currentThread = thread;
            }
            SaveState();
            UpdateChatSectionHeading();
            RefreshChatSelection();
            SetStatus(archived ? "Чат в архиве" : "Чат восстановлен", thread.Title);
        }
        catch (Exception error)
        {
            SetStatus("Не удалось изменить чат", error.Message, isError: true);
        }
    }

    private async Task DeleteThreadAsync(ChatThread thread)
    {
        if (_isRunning) return;
        var result = MessageBox.Show(this,
            $"Удалить чат «{thread.Title}»? Его история будет удалена из Codex CLI и приложения. Это действие нельзя отменить.",
            "Удалить чат", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (thread.CodexSessionId is { Length: > 0 } sessionId)
                await RunSessionCommandAsync("delete", sessionId);
            _threads.Remove(thread);
            if (_currentThread == thread)
                _currentThread = _threads.LastOrDefault(item => item.ProjectId == thread.ProjectId && item.IsArchived == _showArchivedChats);
            SaveState();
            UpdateChatSectionHeading();
            RefreshChatSelection();
            SetStatus("Чат удален", thread.Title);
        }
        catch (Exception error)
        {
            SetStatus("Не удалось удалить чат", error.Message, isError: true);
        }
    }

    private static async Task RunSessionCommandAsync(string command, string sessionId)
    {
        var startInfo = new ProcessStartInfo("codex")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(command);
        if (command == "delete") startInfo.ArgumentList.Add("--force");
        startInfo.ArgumentList.Add(sessionId);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить codex CLI.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var errors = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors) ? output.Trim() : errors.Trim());
    }

    private async Task RefreshUsageLimitsAsync()
    {
        if (_isRefreshingUsage || !IsLoaded) return;
        _isRefreshingUsage = true;
        try
        {
            var snapshot = await ReadUsageLimitsAsync();
            _usageLimitsText = FormatUsageLimits(snapshot);
            _usageLimitsTooltip = $"Лимиты обновлены {DateTime.Now:HH:mm}";
        }
        catch (Exception error)
        {
            _usageLimitsText = "Лимиты недоступны";
            _usageLimitsTooltip = error.Message;
        }
        finally
        {
            _isRefreshingUsage = false;
            if (Control<TextBlock>("UsageLimitsLabel") is { } label)
            {
                label.Text = _usageLimitsText;
                label.ToolTip = _usageLimitsTooltip;
            }
        }
    }

    private static async Task<UsageSnapshot> ReadUsageLimitsAsync()
    {
        var startInfo = new ProcessStartInfo("codex")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить Codex app-server.");
        var errorsTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codexgui\",\"title\":\"CodexGUI\",\"version\":\"1.0\"}}}");
            _ = await ReadRpcResultAsync(process.StandardOutput, 1);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}");
            await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{}}");
            var result = await ReadRpcResultAsync(process.StandardOutput, 2);
            return ParseUsageSnapshot(result);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); }
            }
            await errorsTask;
        }
    }

    private Task RestoreThreadHistoryAsync(ChatThread thread)
    {
        if (thread.Messages.Count > 0)
        {
            thread.HistoryLoaded = true;
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(thread.CodexSessionId) || thread.HistoryLoaded)
            return Task.CompletedTask;
        if (thread.HistoryRestoreTask is { IsCompleted: false } inProgress)
            return inProgress;

        thread.HistoryRestoreTask = RestoreThreadHistoryCoreAsync(thread, thread.CodexSessionId);
        return thread.HistoryRestoreTask;
    }

    private async Task RestoreThreadHistoryCoreAsync(ChatThread thread, string sessionId)
    {
        thread.HistoryLoading = true;
        thread.HistoryError = null;
        if (_currentThread == thread)
        {
            RenderConversation();
            SetStatus("Загружаю историю чата…", "Читаю сообщения из сохранённой сессии Codex.");
        }

        try
        {
            var result = await ReadThreadHistoryAsync(sessionId);
            thread.Messages.Clear();
            thread.Messages.AddRange(ParseThreadMessages(result));
            thread.HistoryLoaded = true;
        }
        catch (Exception error)
        {
            thread.HistoryError = error.Message;
            if (_currentThread == thread)
                SetStatus("Не удалось загрузить историю", error.Message, isError: true);
        }
        finally
        {
            thread.HistoryLoading = false;
            thread.HistoryRestoreTask = null;
            if (_currentThread == thread)
            {
                RenderConversation();
                if (thread.HistoryError is null)
                    SetStatus("Codex CLI готов", "История чата восстановлена из Codex.");
            }
        }
    }

    private static async Task<JsonElement> ReadThreadHistoryAsync(string sessionId)
    {
        var startInfo = new ProcessStartInfo("codex")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить Codex app-server для чтения истории.");
        var errorsTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codexgui\",\"title\":\"CodexGUI\",\"version\":\"1.0\"}}}");
            _ = await ReadRpcResultAsync(process.StandardOutput, 1);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}");
            var request = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = 2,
                ["method"] = "thread/read",
                ["params"] = new { threadId = sessionId, includeTurns = true }
            });
            await process.StandardInput.WriteLineAsync(request);
            return await ReadRpcResultAsync(process.StandardOutput, 2);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); }
            }
            await errorsTask;
        }
    }

    private static List<ChatMessage> ParseThreadMessages(JsonElement result)
    {
        if (!result.TryGetProperty("thread", out var thread) || !thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Codex не вернул историю ходов чата.");

        var messages = new List<ChatMessage>();
        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                switch (GetString(item, "type"))
                {
                    case "agentMessage":
                        if (GetString(item, "text") is { Length: > 0 } answer)
                            messages.Add(new ChatMessage(false, answer));
                        break;
                    case "userMessage":
                        var prompt = ReadUserMessageText(item);
                        if (!string.IsNullOrWhiteSpace(prompt)) messages.Add(new ChatMessage(true, prompt));
                        break;
                }
            }
        }
        return messages;
    }

    private static string ReadUserMessageText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "";
        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            switch (GetString(part, "type"))
            {
                case "text" when GetString(part, "text") is { Length: > 0 } text:
                    parts.Add(text);
                    break;
                case "localImage" when GetString(part, "path") is { Length: > 0 } path:
                    parts.Add($"[Изображение: {path}]");
                    break;
                case "image":
                    parts.Add("[Изображение]");
                    break;
            }
        }
        return string.Join(Environment.NewLine, parts);
    }

    private static async Task<JsonElement> ReadRpcResultAsync(StreamReader reader, int requestId)
    {
        while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() != requestId)
                continue;
            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException(GetString(error, "message") ?? "Codex app-server отклонил запрос.");
            if (root.TryGetProperty("result", out var result)) return result.Clone();
            throw new InvalidDataException("Codex app-server вернул пустой ответ.");
        }
        throw new EndOfStreamException("Codex app-server завершился до ответа о лимитах.");
    }

    private static UsageSnapshot ParseUsageSnapshot(JsonElement result)
    {
        JsonElement rateLimits = default;
        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimitId) &&
            byLimitId.ValueKind == JsonValueKind.Object && byLimitId.TryGetProperty("codex", out var codexLimits))
        {
            rateLimits = codexLimits;
        }
        else if (result.TryGetProperty("rateLimits", out var legacyLimits) && GetString(legacyLimits, "limitId") == "codex")
        {
            rateLimits = legacyLimits;
        }

        if (rateLimits.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("В ответе Codex нет лимитов для Codex.");

        var windows = new List<UsageWindow>();
        AddUsageWindow(rateLimits, "primary", windows);
        AddUsageWindow(rateLimits, "secondary", windows);
        return new UsageSnapshot(windows.OrderBy(window => window.DurationMinutes ?? long.MaxValue).ToArray());
    }

    private static void AddUsageWindow(JsonElement limits, string property, ICollection<UsageWindow> windows)
    {
        if (!limits.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number)
            return;
        long? duration = item.TryGetProperty("windowDurationMins", out var minutes) && minutes.ValueKind == JsonValueKind.Number
            ? minutes.GetInt64()
            : null;
        long? resetsAt = item.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number
            ? reset.GetInt64()
            : null;
        windows.Add(new UsageWindow(duration, Math.Clamp(used.GetInt32(), 0, 100), resetsAt));
    }

    private static string FormatUsageLimits(UsageSnapshot snapshot)
    {
        if (snapshot.Windows.Count == 0) return "Данные о лимитах отсутствуют";
        return string.Join("   ·   ", snapshot.Windows.Select(window =>
        {
            var period = window.DurationMinutes switch
            {
                300 => "5 ч",
                10080 => "Неделя",
                { } minutes when minutes % 1440 == 0 => $"{minutes / 1440} д",
                { } minutes when minutes % 60 == 0 => $"{minutes / 60} ч",
                { } minutes => $"{minutes} мин",
                _ => "Лимит"
            };
            var reset = window.ResetsAt is { } timestamp
                ? DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("dd.MM HH:mm")
                : null;
            return reset is null
                ? $"{period}: {100 - window.UsedPercent}% осталось"
                : $"{period}: {100 - window.UsedPercent}% осталось (сброс {reset})";
        }));
    }

    private void RefreshChatSelection()
    {
        if (Control<TextBlock>("HeaderTitle") is { } title) title.Text = _currentThread?.Title ?? "Новая задача";
        RenderThreads();
        RenderConversation();
        if (_currentThread is not null) _ = RestoreThreadHistoryAsync(_currentThread);
    }

    private void RenderProjects()
    {
        if (Control<StackPanel>("ProjectList") is not { } list) return;
        list.Children.Clear();
        foreach (var project in _projects)
        {
            var isSelected = project.Id == _selectedProject?.Id;
            var row = new Grid { Height = 35, Margin = new Thickness(0, 1, 0, 1) };
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(9, 7, 38, 7),
                Background = isSelected ? Brush("#27292E") : Brushes.Transparent,
                Foreground = Brush(isSelected ? "#F0F0F1" : "#A4A5AA"),
                ToolTip = project.Path,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = "⌘", FontSize = 12, Foreground = Brush(isSelected ? "#D7D8DC" : "#84868D"), Margin = new Thickness(0, 0, 9, 0) },
                        new TextBlock { Text = project.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 168 }
                    }
                }
            };
            button.Click += (_, _) => SelectProject(project);
            var newChat = new Button
            {
                Width = 26,
                Height = 25,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                Foreground = Brush(isSelected ? "#C7C8CC" : "#85868C"),
                ToolTip = $"Создать чат в проекте «{project.Name}»",
                Content = new System.Windows.Shapes.Path
                {
                    Width = 14,
                    Height = 14,
                    Stretch = Stretch.Uniform,
                    Stroke = Brush(isSelected ? "#C7C8CC" : "#85868C"),
                    StrokeThickness = 1.25,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Data = Geometry.Parse("M13.5,2.5 H5 A2.5,2.5 0 0 0 2.5,5 V14 A2.5,2.5 0 0 0 5,16.5 H11 M11.5,13.5 L15.5,9.5 M12.5,5.5 L16.5,1.5 L18.5,3.5 L14.5,7.5 L11,8.5 Z")
                }
            };
            newChat.Click += (_, _) =>
            {
                SelectProject(project);
                NewChat();
            };
            row.Children.Add(button);
            row.Children.Add(newChat);
            list.Children.Add(row);
        }
    }

    private void RenderConversation()
    {
        if (Control<StackPanel>("Conversation") is not { } stack) return;
        _streamingAssistantTextBlock = null;
        stack.Children.Clear();
        if (_currentThread is null || _currentThread.Messages.Count == 0)
        {
            var welcome = new StackPanel { Margin = new Thickness(0, 56, 0, 0) };
            welcome.Children.Add(new TextBlock { Text = "✳", FontSize = 25, Foreground = Brush("#E5E5E7"), Margin = new Thickness(0, 0, 0, 15) });
            welcome.Children.Add(new TextBlock { Text = _currentThread?.Title ?? $"Работаем над проектом «{_selectedProject?.Name ?? "CodexGUI"}»", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Brush("#E7E7E9"), TextWrapping = TextWrapping.Wrap });
            var detail = _currentThread?.HistoryLoading == true
                ? "Загружаю историю из Codex…"
                : _currentThread?.HistoryError is { } historyError
                    ? $"Не удалось восстановить историю: {historyError}"
                    : _currentThread?.CodexSessionId is not null
                ? "Этот чат сохранён в Codex. Отправьте сообщение, чтобы продолжить разговор."
                : "Опишите задачу — Codex поможет разобраться в проекте, написать код или внести изменения.";
            welcome.Children.Add(new TextBlock { Text = detail, FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#9A9BA1"), Margin = new Thickness(0, 9, 0, 20), MaxWidth = 600 });
            if (_currentThread is null)
            {
                welcome.Children.Add(CreateSuggestion("Разберись в структуре проекта"));
                welcome.Children.Add(CreateSuggestion("Помоги найти и исправить ошибку"));
                welcome.Children.Add(CreateSuggestion("Предложи улучшения для кода"));
            }
            stack.Children.Add(welcome);
        }
        else
        {
            foreach (var message in _currentThread.Messages) stack.Children.Add(CreateMessage(message));
        }
        if (Control<ScrollViewer>("ConversationScroll") is { } scroll)
            Dispatcher.BeginInvoke(() => scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private FrameworkElement CreateSuggestion(string text)
    {
        var button = new Button
        {
            Content = text + "     ↗",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brush("#1A1B1F"),
            Foreground = Brush("#BFC0C4"),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 3, 0, 3),
            Width = 310
        };
        button.Click += async (_, _) =>
        {
            if (Control<TextBox>("PromptInput") is { } input) input.Text = text;
            await SendPromptAsync();
        };
        return button;
    }

    private FrameworkElement CreateMessage(ChatMessage message)
    {
        var outer = new StackPanel
        {
            HorizontalAlignment = message.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 700,
            Margin = new Thickness(0, 10, 0, 13)
        };
        if (!message.IsUser)
            outer.Children.Add(new TextBlock { Text = "✳  Codex", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("#C4C5C9"), Margin = new Thickness(1, 0, 0, 8) });

        var text = new TextBlock
        {
            Text = message.Text,
            FontSize = 14,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(message.IsUser ? "#E6E6E8" : "#D2D3D6")
        };
        if (!message.IsUser && _isRunning && ReferenceEquals(_currentThread?.Messages.LastOrDefault(), message))
            _streamingAssistantTextBlock = text;
        if (message.IsUser)
        {
            var bubble = new Border { Background = Brush("#292B30"), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 10), Child = text };
            outer.Children.Add(bubble);
        }
        else outer.Children.Add(text);
        return outer;
    }

    private void RenderFileTree()
    {
        if (Control<TreeView>("FileTree") is not { } tree) return;
        tree.Items.Clear();
        var root = new TreeViewItem { Header = Path.GetFileName(_projectPath.TrimEnd(Path.DirectorySeparatorChar)), IsExpanded = true };
        tree.Items.Add(root);
        if (!Directory.Exists(_projectPath)) return;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_projectPath)
                         .Where(path => !new[] { ".git", "bin", "obj", "node_modules", ".vs" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(35))
                root.Items.Add(new TreeViewItem { Header = "▸  " + Path.GetFileName(directory), Foreground = Brush("#BFC0C4") });

            foreach (var file in Directory.EnumerateFiles(_projectPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(80))
                root.Items.Add(new TreeViewItem { Header = "·  " + Path.GetFileName(file), Foreground = Brush("#A4A5AA"), ToolTip = file });
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RefreshFileTree() => RenderFileTree();

    private void ScheduleStateSave()
    {
        _stateSaveDebounce.Stop();
        _stateSaveDebounce.Start();
    }

    private async Task SendPromptAsync()
    {
        if (_isRunning || Control<TextBox>("PromptInput") is not { } input) return;
        var prompt = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || prompt == PromptPlaceholder) return;
        if (_currentThread is null && _selectedProject is not null)
            _projectPath = _selectedProject.Path;
        if (!Directory.Exists(_projectPath))
        {
            MessageBox.Show(this, "Выберите существующую папку проекта.", "Codex", MessageBoxButton.OK, MessageBoxImage.Information);
            ChooseProjectFolder();
            return;
        }

        if (_currentThread?.IsArchived == true)
        {
            var archivedThread = _currentThread;
            await SetThreadArchivedAsync(archivedThread, archived: false);
            if (archivedThread.IsArchived) return;
        }
        _showArchivedChats = false;
        UpdateChatSectionHeading();
        _currentThread ??= NewThread();
        var thread = _currentThread;
        if (thread.CodexSessionId is not null && thread.Messages.Count == 0)
        {
            await RestoreThreadHistoryAsync(thread);
            if (!thread.HistoryLoaded) return;
        }
        if (thread.Messages.Count == 0) thread.Title = TrimTitle(prompt);
        thread.Messages.Add(new ChatMessage(true, prompt));
        var assistantMessage = new ChatMessage(false, "Думаю…");
        thread.Messages.Add(assistantMessage);
        SaveState();
        input.Clear();
        _isRunning = true;
        SetStatus("Codex работает…", "Запрос выполняется в выбранной папке.");
        RenderThreads();
        RenderConversation();
        if (Control<Button>("SendButton") is { } send) send.IsEnabled = false;

        try
        {
            var response = await RunCodexAsync(thread, prompt, assistantMessage);
            assistantMessage.Text = response;
            SaveState();
            if (string.IsNullOrWhiteSpace(response)) assistantMessage.Text = "Codex завершил запрос без текстового ответа. Проверьте детали команды в терминале.";
            SetStatus("Codex CLI готов", "Ответ получен.");
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Text = "Запрос остановлен.";
            SetStatus("Запрос остановлен", "Можно отправить следующий запрос.");
        }
        catch (Exception error)
        {
            assistantMessage.Text = "Не удалось выполнить запрос:\n" + error.Message;
            SetStatus("Ошибка запроса", error.Message, isError: true);
        }
        finally
        {
            _isRunning = false;
            _activeProcess?.Dispose();
            _activeProcess = null;
            if (Control<Button>("SendButton") is { } sendButton) sendButton.IsEnabled = true;
            RenderThreads();
            if (_currentThread == thread) RenderConversation();
            _ = RefreshUsageLimitsAsync();
        }
    }

    private async Task<string> RunCodexAsync(ChatThread thread, string prompt, ChatMessage assistantMessage)
    {
        var startInfo = new ProcessStartInfo("codex")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = thread.ProjectPath
        };

        if (IsFullAccess)
        {
            startInfo.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
        }
        else
        {
            startInfo.ArgumentList.Add("--ask-for-approval");
            startInfo.ArgumentList.Add("on-request");
        }
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(_selectedModel);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add($"model_reasoning_effort={_selectedEffort}");
        startInfo.ArgumentList.Add("exec");
        if (thread.CodexSessionId is not null)
        {
            startInfo.ArgumentList.Add("resume");
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add(thread.CodexSessionId);
            startInfo.ArgumentList.Add(prompt);
        }
        else
        {
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("--skip-git-repo-check");
            if (!IsFullAccess)
            {
                startInfo.ArgumentList.Add("--sandbox");
                startInfo.ArgumentList.Add("workspace-write");
            }
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(thread.ProjectPath);
            startInfo.ArgumentList.Add(prompt);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить codex CLI.");
        _activeProcess = process;
        var response = new StringBuilder();
        var errors = new StringBuilder();
        var responseLock = new object();
        var updateQueued = 0;

        void QueueResponseRefresh()
        {
            if (Interlocked.Exchange(ref updateQueued, 1) != 0) return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref updateQueued, 0);
                string text;
                lock (responseLock) text = response.ToString();
                if (!_isRunning) return;
                if (thread.Messages.Count == 0 || !ReferenceEquals(thread.Messages[^1], assistantMessage)) return;
                assistantMessage.Text = text;
                if (_currentThread == thread && _streamingAssistantTextBlock is { } messageText)
                {
                    messageText.Text = text;
                    if (Control<ScrollViewer>("ConversationScroll") is { } scroll)
                        scroll.ScrollToEnd();
                }
            }, DispatcherPriority.Background);
        }

        void AppendResponse(string text, bool skipIfAlreadyEmitted = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (responseLock)
            {
                if (skipIfAlreadyEmitted && response.ToString().EndsWith(text, StringComparison.Ordinal)) return;
                response.Append(text);
            }
            QueueResponseRefresh();
        }

        var outputTask = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var json = JsonDocument.Parse(line);
                        var root = json.RootElement;
                        var eventType = GetString(root, "type");
                        if (eventType == "thread.started" && GetString(root, "thread_id") is { } id)
                        {
                            _ = Dispatcher.BeginInvoke(() =>
                            {
                                thread.CodexSessionId = id;
                                SaveState();
                            }, DispatcherPriority.Background);
                        }

                        if (eventType == "token_count" && root.TryGetProperty("info", out var tokenInfo) &&
                            tokenInfo.TryGetProperty("last_token_usage", out var lastUsage) &&
                            lastUsage.TryGetProperty("total_tokens", out var totalTokens) && totalTokens.TryGetInt64(out var tokenCount) && tokenCount >= 0 &&
                            tokenInfo.TryGetProperty("model_context_window", out var contextWindow) && contextWindow.TryGetInt64(out var windowSize) && windowSize > 0)
                        {
                            _ = Dispatcher.BeginInvoke(() =>
                            {
                                thread.ContextTokens = tokenCount;
                                thread.ContextWindowTokens = windowSize;
                                ScheduleStateSave();
                                if (_currentThread == thread) UpdateContextIndicator();
                            }, DispatcherPriority.Background);
                        }

                        if (eventType == "item.completed" && root.TryGetProperty("item", out var item) && GetString(item, "type") == "agent_message")
                        {
                            var text = GetString(item, "text");
                            if (!string.IsNullOrEmpty(text)) AppendResponse(text, skipIfAlreadyEmitted: true);
                        }
                        else if (eventType == "response.output_text.delta" && GetString(root, "delta") is { } delta)
                            AppendResponse(delta);
                    }
                    catch (JsonException)
                    {
                        AppendResponse(line + Environment.NewLine);
                    }
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // A descendant process can keep the inherited pipe open after Codex exits.
            }
        });
        var errorTask = Task.Run(async () =>
        {
            try { errors.Append(await process.StandardError.ReadToEndAsync()); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        });
        await process.WaitForExitAsync();
        var readersTask = Task.WhenAll(outputTask, errorTask);
        if (await Task.WhenAny(readersTask, Task.Delay(TimeSpan.FromSeconds(3))) != readersTask)
        {
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
        }
        await readersTask;
        _activeProcess = null;

        if (process.ExitCode != 0)
        {
            var detail = errors.ToString().Trim();
            if (string.IsNullOrEmpty(detail))
            {
                lock (responseLock) detail = response.ToString().Trim();
            }
            throw new InvalidOperationException(string.IsNullOrEmpty(detail) ? $"codex завершился с кодом {process.ExitCode}." : detail);
        }
        lock (responseLock) return response.ToString().Trim();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void UpdateContextIndicator()
    {
        if (Control<TextBlock>("ContextPercent") is not { } label || Control<System.Windows.Shapes.Path>("ContextUsageArc") is not { } arc)
            return;

        var tokens = _currentThread?.ContextTokens;
        var window = _currentThread?.ContextWindowTokens;
        if (tokens is null || window is null || window <= 0)
        {
            label.Text = "—";
            label.ToolTip = "Контекст появится после первого ответа Codex";
            arc.Data = Geometry.Empty;
            arc.Stroke = Brush("#77C99B");
            return;
        }

        const long baselineTokens = 12_000;
        if (window.Value <= baselineTokens)
        {
            label.Text = "0%";
            label.ToolTip = "Для этой модели нет доступного окна контекста";
            arc.Data = Geometry.Empty;
            arc.Stroke = Brush("#E78C8C");
            return;
        }

        var effectiveWindow = window.Value - baselineTokens;
        var usedTokens = Math.Max(tokens.Value - baselineTokens, 0);
        var remainingFraction = Math.Clamp((double)(effectiveWindow - Math.Min(usedTokens, effectiveWindow)) / effectiveWindow, 0d, 1d);
        var remainingPercent = (int)Math.Round(remainingFraction * 100, MidpointRounding.AwayFromZero);
        label.Text = remainingPercent.ToString(CultureInfo.InvariantCulture) + "%";
        label.ToolTip = $"Осталось {remainingPercent}% контекста · использовано {tokens.Value.ToString("N0", CultureInfo.CurrentCulture)} из {window.Value.ToString("N0", CultureInfo.CurrentCulture)} токенов";
        arc.Stroke = Brush(remainingPercent <= 15 ? "#E78C8C" : remainingPercent <= 35 ? "#E6BC70" : "#77C99B");

        const double center = 18;
        const double radius = 14;
        if (remainingFraction <= 0)
        {
            arc.Data = Geometry.Empty;
            return;
        }
        if (remainingFraction >= 1)
        {
            arc.Data = new EllipseGeometry(new Point(center, center), radius, radius);
            return;
        }

        var angle = remainingFraction * Math.PI * 2;
        var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(center, center - radius), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, remainingFraction > 0.5, SweepDirection.Clockwise, true));
        arc.Data = new PathGeometry([figure]);
    }

    private ChatThread NewThread()
    {
        var project = _selectedProject ?? throw new InvalidOperationException("Сначала выберите или создайте проект.");
        var thread = new ChatThread { Title = "Новый чат", ProjectId = project.Id, ProjectPath = project.Path };
        _threads.Add(thread);
        _currentThread = thread;
        SaveState();
        return thread;
    }

    private void NewChat()
    {
        _showArchivedChats = false;
        _currentThread = NewThread();
        UpdateChatSectionHeading();
        if (Control<TextBlock>("HeaderTitle") is { } title) title.Text = "Новая задача";
        RenderThreads();
        RenderConversation();
        UpdateContextIndicator();
        Control<TextBox>("PromptInput")?.Focus();
    }

    private void SelectThread(ChatThread thread)
    {
        _currentThread = thread;
        _selectedProject = _projects.FirstOrDefault(project => project.Id == thread.ProjectId);
        if (_selectedProject is not null) _projectPath = _selectedProject.Path;
        if (Control<TextBlock>("HeaderTitle") is { } title) title.Text = thread.Title;
        RefreshView();
        SaveState();
        _ = RestoreThreadHistoryAsync(thread);
    }

    private void SelectProject(ProjectEntry project)
    {
        _selectedProject = project;
        _projectPath = project.Path;
        _showArchivedChats = false;
        _currentThread = _threads.LastOrDefault(thread => thread.ProjectId == project.Id && !thread.IsArchived);
        if (Control<TextBlock>("HeaderTitle") is { } title) title.Text = _currentThread?.Title ?? "Новая задача";
        RefreshView();
        SaveState();
        if (_currentThread is not null) _ = RestoreThreadHistoryAsync(_currentThread);
    }

    private void ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        if (_activeView is { ColumnDefinitions.Count: 3 } view)
            view.ColumnDefinitions[0].Width = _sidebarCollapsed ? new GridLength(0) : new GridLength(250);
    }

    private void ToggleContext(bool? open = null)
    {
        _contextOpen = open ?? !_contextOpen;
        if (_activeView is { ColumnDefinitions.Count: 3 } view)
            view.ColumnDefinitions[2].Width = _contextOpen ? new GridLength(310) : new GridLength(0);
        if (Control<Border>("ContextPanel") is { } panel) panel.Visibility = _contextOpen ? Visibility.Visible : Visibility.Collapsed;
        if (_contextOpen) RefreshFileTree();
    }

    private void ShowAccessMenu()
    {
        if (Control<Button>("ModeButton") is not { } anchor) return;

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            HorizontalOffset = 0,
            VerticalOffset = -8
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "Как Codex может выполнять действия?",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F0F0F1"),
            Margin = new Thickness(5, 2, 5, 11)
        });
        content.Children.Add(BuildAccessOption(
            "Попросить подтверждение",
            "Можно менять файлы проекта. Для действий за его пределами Codex запросит разрешение.",
            selected: !IsFullAccess,
            elevated: false,
            () =>
            {
                popup.IsOpen = false;
                SetProjectAccessMode(false);
                ApplyAccessMode();
            }));
        content.Children.Add(BuildAccessOption(
            "Полный доступ",
            "Без ограничений на файлы и сеть. Команды выполняются без дополнительных запросов.",
            selected: IsFullAccess,
            elevated: true,
            () =>
            {
                popup.IsOpen = false;
                var result = MessageBox.Show(this,
                    "Codex получит неограниченный доступ к файлам компьютера и сети. Команды будут выполняться без дополнительных подтверждений. Включать полный доступ следует только для доверенных задач.\n\nПродолжить?",
                    "Подтверждение полного доступа",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
                SetProjectAccessMode(true);
                ApplyAccessMode();
            }));

        popup.Child = new Border
        {
            Width = 430,
            MinHeight = 170,
            Background = Brush("#202124"),
            BorderBrush = Brush("#3B3D42"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 5, Opacity = 0.38 },
            Child = content
        };
        popup.IsOpen = true;
    }

    private Border BuildAccessOption(string title, string description, bool selected, bool elevated, Action onClick)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = Brush(elevated ? "#F0A277" : "#E3E4E6")
        };
        var descriptionText = new TextBlock
        {
            Text = description,
            FontSize = 11,
            Foreground = Brush("#A0A1A7"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 8, 0),
            MaxWidth = 340
        };
        var copy = new StackPanel();
        copy.Children.Add(titleText);
        copy.Children.Add(descriptionText);
        var indicator = new TextBlock
        {
            Text = selected ? "✓" : elevated ? "⚠" : "",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(elevated ? "#F0A277" : "#D7D8DC"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(copy);
        Grid.SetColumn(indicator, 1);
        layout.Children.Add(indicator);

        var option = new Border
        {
            Background = selected ? Brush("#292B30") : Brushes.Transparent,
            BorderBrush = selected ? Brush("#46484F") : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 9, 10, 9),
            Margin = new Thickness(0, 2, 0, 2),
            Cursor = Cursors.Hand,
            Child = layout
        };
        option.MouseEnter += (_, _) => option.Background = Brush("#292B30");
        option.MouseLeave += (_, _) => option.Background = selected ? Brush("#292B30") : Brushes.Transparent;
        option.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return option;
    }

    private async Task ShowModelMenuAsync()
    {
        if (Control<Button>("ModelButton") is not { } anchor) return;
        await EnsureModelCatalogAsync(anchor);
        if (_availableModels.Count == 0) return;

        var choices = _availableModels.Select(model =>
            (model.DisplayName, model.Slug, _selectedModel == model.Slug, (Action)(() => SetSelectedModel(model.Slug)))).ToList();
        ShowChoicePopup(anchor, "Модель", choices);
    }

    private async Task ShowEffortMenuAsync()
    {
        if (Control<Button>("EffortButton") is not { } anchor) return;
        await EnsureModelCatalogAsync(anchor);
        if (_availableModels.Count == 0) return;

        var model = _availableModels.FirstOrDefault(item => item.Slug == _selectedModel)
            ?? throw new InvalidDataException($"Выбранная модель {_selectedModel} отсутствует в каталоге Codex CLI.");
        var choices = model.ReasoningLevels.Select(level =>
            (EffortDisplayName(level), level, _selectedEffort == level, (Action)(() => SetSelectedEffort(level)))).ToList();
        ShowChoicePopup(anchor, "Уровень усилий", choices);
    }

    private async Task EnsureModelCatalogAsync(Button anchor)
    {
        if (_availableModels.Count > 0) return;
        var label = Control<TextBlock>(anchor.Name == "ModelButton" ? "ModelLabel" : "EffortLabel");
        var previousText = label?.Text;
        if (label is not null) label.Text = "Загрузка…";
        anchor.IsEnabled = false;
        try
        {
            _modelCatalogTask ??= LoadModelCatalogAsync();
            _availableModels = await _modelCatalogTask;
            if (_availableModels.Count == 0)
                throw new InvalidDataException("Codex CLI не сообщил доступные модели.");
            NormalizeModelSettings();
        }
        catch (Exception error)
        {
            SetStatus("Не удалось загрузить модели", error.Message, isError: true);
        }
        finally
        {
            _modelCatalogTask = null;
            anchor.IsEnabled = true;
            if (label is not null) label.Text = previousText;
            UpdateComposerSettingsLabels();
        }
    }

    private static async Task<IReadOnlyList<ModelOption>> LoadModelCatalogAsync()
    {
        var startInfo = new ProcessStartInfo("codex")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("debug");
        startInfo.ArgumentList.Add("models");
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить codex debug models.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var errorOutput = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorOutput)
                ? $"codex debug models завершился с кодом {process.ExitCode}."
                : errorOutput.Trim());

        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("models", out var models))
            throw new InvalidDataException("Ответ Codex CLI не содержит каталог моделей.");

        return models.EnumerateArray()
            .Where(item => GetString(item, "visibility") == "list" && item.TryGetProperty("supported_in_api", out var supported) && supported.GetBoolean())
            .Select(item => new ModelOption(
                GetString(item, "slug") ?? "",
                GetString(item, "display_name") ?? GetString(item, "slug") ?? "",
                GetString(item, "default_reasoning_level") ?? DefaultEffort,
                item.TryGetProperty("supported_reasoning_levels", out var efforts)
                    ? efforts.EnumerateArray().Select(effort => GetString(effort, "effort")).Where(effort => effort is not null).Cast<string>().ToArray()
                    : [],
                item.TryGetProperty("priority", out var priority) && priority.TryGetInt32(out var order) ? order : int.MaxValue))
            .Where(model => !string.IsNullOrWhiteSpace(model.Slug) && model.ReasoningLevels.Count > 0)
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void ShowChoicePopup(Button anchor, string title, IReadOnlyList<(string Title, string Description, bool Selected, Action Select)> choices)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            VerticalOffset = -8
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F0F0F1"),
            Margin = new Thickness(5, 2, 5, 9)
        });
        var options = new StackPanel();
        foreach (var choice in choices)
            options.Children.Add(BuildAccessOption(choice.Title, choice.Description, choice.Selected, elevated: false, () =>
            {
                popup.IsOpen = false;
                choice.Select();
            }));
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 500,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = options
        });
        popup.Child = new Border
        {
            Width = 360,
            Background = Brush("#202124"),
            BorderBrush = Brush("#3B3D42"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 5, Opacity = 0.38 },
            Child = content
        };
        popup.IsOpen = true;
    }

    private void SetSelectedModel(string model)
    {
        _selectedModel = model;
        var selected = _availableModels.FirstOrDefault(item => item.Slug == model);
        if (selected is not null && !selected.ReasoningLevels.Contains(_selectedEffort, StringComparer.Ordinal))
            _selectedEffort = selected.ReasoningLevels.Contains(selected.DefaultReasoningLevel, StringComparer.Ordinal)
                ? selected.DefaultReasoningLevel
                : selected.ReasoningLevels.First();
        UpdateComposerSettingsLabels();
        SaveState();
    }

    private void SetSelectedEffort(string effort)
    {
        _selectedEffort = effort;
        UpdateComposerSettingsLabels();
        SaveState();
    }

    private void UpdateComposerSettingsLabels()
    {
        if (Control<TextBlock>("ModelLabel") is { } modelLabel)
            modelLabel.Text = _availableModels.FirstOrDefault(item => item.Slug == _selectedModel)?.DisplayName ?? _selectedModel;
        if (Control<TextBlock>("EffortLabel") is { } effortLabel)
            effortLabel.Text = EffortDisplayName(_selectedEffort);
    }

    private static string EffortDisplayName(string effort) => effort switch
    {
        "low" => "Низкие",
        "medium" => "Средние",
        "high" => "Высокие",
        "xhigh" => "Очень высокие",
        "max" => "Максимальные",
        "ultra" => "Ультра",
        _ => effort
    };

    private void NormalizeModelSettings()
    {
        var model = _availableModels.FirstOrDefault(item => item.Slug == _selectedModel)
            ?? _availableModels.FirstOrDefault(item => item.Slug == DefaultModel)
            ?? _availableModels[0];
        _selectedModel = model.Slug;
        if (!model.ReasoningLevels.Contains(_selectedEffort, StringComparer.Ordinal))
            _selectedEffort = model.ReasoningLevels.Contains(model.DefaultReasoningLevel, StringComparer.Ordinal)
                ? model.DefaultReasoningLevel
                : model.ReasoningLevels.First();
        UpdateComposerSettingsLabels();
        SaveState();
    }

    private void ApplyAccessMode()
    {
        if (Control<TextBlock>("AccessModeLabel") is { } label)
        {
            label.Text = IsFullAccess ? "Полный доступ" : "Попросить подтверждение";
            label.Foreground = Brush(IsFullAccess ? "#F0A277" : "#C4C5C9");
        }
        SaveState();
        SetStatus(IsFullAccess ? "Полный доступ" : "Попросить подтверждение",
            IsFullAccess
                ? "Без ограничений на файлы и сеть; команды выполняются без подтверждений."
                : "Изменения в папке проекта; для выхода за её пределы Codex сможет запросить разрешение.",
            isError: false);
    }

    private bool IsFullAccess => _selectedProject?.FullAccess == true;

    private void SetProjectAccessMode(bool fullAccess)
    {
        if (_selectedProject is not null) _selectedProject.FullAccess = fullAccess;
    }

    private void ShowProjectMenu()
    {
        var menu = new ContextMenu();
        var create = new MenuItem { Header = "Создать проект…" };
        create.Click += (_, _) => CreateProject();
        var addExisting = new MenuItem { Header = "Добавить папку проекта…" };
        addExisting.Click += (_, _) => ChooseProjectFolder();
        menu.Items.Add(create);
        menu.Items.Add(addExisting);
        menu.PlacementTarget = Control<Button>("AddProjectButton");
        menu.IsOpen = true;
    }

    private void CreateProject()
    {
        var picker = new OpenFolderDialog
        {
            Title = "Где создать папку проекта?",
            InitialDirectory = Directory.Exists(_projectPath) ? _projectPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (picker.ShowDialog(this) != true) return;

        var name = AskForProjectName();
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            MessageBox.Show(this, "В названии есть недопустимые символы.", "Новый проект", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var path = Path.Combine(picker.FolderName, name);
        if (Directory.Exists(path) || File.Exists(path))
        {
            MessageBox.Show(this, "Папка или файл с таким именем уже существует. Выберите другое имя.", "Новый проект", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            AddProjectPath(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "Не удалось создать проект", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? AskForProjectName()
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "Новый проект",
            Width = 390,
            Height = 178,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("#1A1B1F"),
            Foreground = Brush("#F0F0F1")
        };
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "Название проекта", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 11) });
        var input = new TextBox { Height = 36, Padding = new Thickness(9, 7, 9, 5), Background = Brush("#25272C") };
        content.Children.Add(input);
        var create = new Button { Content = "Создать папку", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Background = Brush("#E9E9EB"), Foreground = Brush("#17181B") };
        create.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        content.Children.Add(create);
        dialog.Content = content;
        dialog.Loaded += (_, _) => input.Focus();
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; }
        };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void AddProjectPath(string path)
    {
        path = Path.GetFullPath(path);
        var existing = _projects.FirstOrDefault(project => string.Equals(project.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectProject(existing);
            return;
        }

        var project = new ProjectEntry { Id = Guid.NewGuid().ToString("N"), Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), Path = path };
        _projects.Add(project);
        SelectProject(project);
    }

    private void LoadState()
    {
        var statePath = GetStatePath();
        if (File.Exists(statePath))
        {
            try
            {
                var state = JsonSerializer.Deserialize<StoredState>(File.ReadAllText(statePath));
                if (state is not null)
                {
                    _projects.AddRange(state.Projects.Where(project => !string.IsNullOrWhiteSpace(project.Id) && !string.IsNullOrWhiteSpace(project.Path)));
                    foreach (var stored in state.Threads)
                    {
                        if (string.IsNullOrWhiteSpace(stored.Id) || string.IsNullOrWhiteSpace(stored.ProjectPath)) continue;
                        _threads.Add(new ChatThread
                        {
                            Id = stored.Id,
                            Title = string.IsNullOrWhiteSpace(stored.Title) ? "Новый чат" : stored.Title,
                            ProjectId = stored.ProjectId,
                            ProjectPath = stored.ProjectPath,
                            CodexSessionId = stored.CodexSessionId,
                            IsArchived = stored.IsArchived,
                            ContextTokens = stored.ContextTokens,
                            ContextWindowTokens = stored.ContextWindowTokens
                        });
                    }
                    _selectedProject = _projects.FirstOrDefault(project => project.Id == state.SelectedProjectId);
                    _selectedModel = string.IsNullOrWhiteSpace(state.SelectedModel) ? DefaultModel : state.SelectedModel;
                    _selectedEffort = string.IsNullOrWhiteSpace(state.SelectedEffort) ? DefaultEffort : state.SelectedEffort;
                }
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                _startupIssue = "Не удалось прочитать список проектов: " + error.Message;
            }
        }

        if (_projects.Count == 0)
        {
            var path = FindProjectRoot();
            _projects.Add(new ProjectEntry { Id = Guid.NewGuid().ToString("N"), Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), Path = path });
            SaveState();
        }
        _selectedProject ??= _projects[0];
        _projectPath = _selectedProject.Path;
        _currentThread = _threads.LastOrDefault(thread => thread.ProjectId == _selectedProject.Id && !thread.IsArchived);
        SaveState();
    }

    private void SaveState()
    {
        try
        {
            var statePath = GetStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            var state = new StoredState
            {
                SelectedProjectId = _selectedProject?.Id,
                SelectedModel = _selectedModel,
                SelectedEffort = _selectedEffort,
                Projects = _projects,
                Threads = _threads.Select(thread => new StoredThread
                {
                    Id = thread.Id,
                    Title = thread.Title,
                    ProjectId = thread.ProjectId,
                    ProjectPath = thread.ProjectPath,
                    CodexSessionId = thread.CodexSessionId,
                    IsArchived = thread.IsArchived,
                    ContextTokens = thread.ContextTokens,
                    ContextWindowTokens = thread.ContextWindowTokens
                }).ToList()
            };
            var temporary = statePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, statePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _startupIssue = "Не удалось сохранить список проектов: " + error.Message;
        }
    }

    private static string GetStatePath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "state.json");

    private void ChooseProjectFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Добавить существующую папку как проект", InitialDirectory = Directory.Exists(_projectPath) ? _projectPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (dialog.ShowDialog(this) != true) return;
        AddProjectPath(dialog.FolderName);
    }

    private void OpenProjectFolder()
    {
        if (!Directory.Exists(_projectPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", _projectPath) { UseShellExecute = true });
    }

    private void AddFileContext()
    {
        var dialog = new OpenFileDialog { Title = "Добавить файл в контекст", InitialDirectory = Directory.Exists(_projectPath) ? _projectPath : null, Multiselect = false };
        if (dialog.ShowDialog(this) != true || Control<TextBox>("PromptInput") is not { } input) return;
        if (input.Text == PromptPlaceholder) input.Clear();
        input.Text = (string.IsNullOrWhiteSpace(input.Text) ? "" : input.Text.TrimEnd() + "\n") + $"Файл для контекста: {dialog.FileName}";
        input.Focus();
        input.CaretIndex = input.Text.Length;
    }

    private async Task CaptureScreenshotAsync()
    {
        using var selector = new ScreenshotSelectionForm();
        if (selector.ShowDialog() != Forms.DialogResult.OK || selector.SelectionBounds.Width < 2 || selector.SelectionBounds.Height < 2)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Сохранить скриншот",
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            FileName = $"CodexGUI-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await Task.Delay(120);
            var bounds = selector.SelectionBounds;
            using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, System.Drawing.CopyPixelOperation.SourceCopy);
            bitmap.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);

            if (Control<TextBox>("PromptInput") is { } input)
            {
                if (input.Text == PromptPlaceholder) input.Clear();
                input.Text = (string.IsNullOrWhiteSpace(input.Text) ? "" : input.Text.TrimEnd() + "\n") + $"Файл для контекста: {dialog.FileName}";
                input.Focus();
                input.CaretIndex = input.Text.Length;
            }
            SetStatus("Скриншот готов", $"Сохранен и добавлен в контекст: {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            MessageBox.Show(this, error.Message, "Не удалось сохранить скриншот", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowSearch()
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "Поиск по чатам",
            Width = 430,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("#1A1B1F"),
            Foreground = Brush("#F0F0F1")
        };
        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(new TextBlock { Text = "Найти чат", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        var query = new TextBox { Height = 38, Padding = new Thickness(10, 8, 10, 5), Background = Brush("#25272C"), Text = "" };
        query.TextChanged += (_, _) =>
        {
            var match = _threads.LastOrDefault(thread => thread.Title.Contains(query.Text, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(query.Text) && match is not null) dialog.Tag = match;
        };
        layout.Children.Add(query);
        var open = new Button { Content = "Открыть чат", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Background = Brush("#34363C") };
        open.Click += (_, _) =>
        {
            if (dialog.Tag is ChatThread match) SelectThread(match);
            dialog.Close();
        };
        layout.Children.Add(open);
        dialog.Content = layout;
        dialog.Loaded += (_, _) => query.Focus();
        dialog.ShowDialog();
    }

    private void SetStatus(string title, string detail, bool isError = false)
    {
        _startupIssue = isError ? detail : null;
        if (_trayIcon is not null) _trayIcon.Text = title.Length > 63 ? title[..60] + "…" : title;
        if (Control<TextBlock>("ConnectionStatus") is { } connection) connection.Text = title;
        if (Control<TextBlock>("ConnectionDetail") is { } connectionDetail) connectionDetail.Text = detail;
        if (Control<TextBlock>("HeaderStatus") is { } header) header.Text = title;
        if (Control<Ellipse>("StatusDot") is { } dot) dot.Fill = Brush(isError ? "#E78C8C" : _isRunning ? "#D7B66E" : "#77C99B");
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static string TrimTitle(string text) => text.Length <= 34 ? text : text[..31].TrimEnd() + "…";

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.K) { NewChat(); e.Handled = true; }
        else if (e.Key == Key.F) { ShowSearch(); e.Handled = true; }
    }

    private async void PromptInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendPromptAsync();
        }
    }

    private void StopCodexProcess()
    {
        try { if (_activeProcess is { HasExited: false }) _activeProcess.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _viewWatcher?.Dispose();
        _trayIcon?.Dispose();
        _trayImage?.Dispose();
    }

    private sealed class ChatThread
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "Новый чат";
        public string ProjectId { get; set; } = "";
        public string ProjectPath { get; set; } = "";
        public string? CodexSessionId { get; set; }
        public bool IsArchived { get; set; }
        public long? ContextTokens { get; set; }
        public long? ContextWindowTokens { get; set; }
        public bool HistoryLoaded { get; set; }
        public bool HistoryLoading { get; set; }
        public string? HistoryError { get; set; }
        public Task? HistoryRestoreTask { get; set; }
        public List<ChatMessage> Messages { get; } = [];
    }

    private sealed class ProjectEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool FullAccess { get; set; }
    }

    private sealed class StoredThread
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string ProjectPath { get; set; } = "";
        public string? CodexSessionId { get; set; }
        public bool IsArchived { get; set; }
        public long? ContextTokens { get; set; }
        public long? ContextWindowTokens { get; set; }
    }

    private sealed class StoredState
    {
        public string? SelectedProjectId { get; set; }
        public string? SelectedModel { get; set; }
        public string? SelectedEffort { get; set; }
        public List<ProjectEntry> Projects { get; set; } = [];
        public List<StoredThread> Threads { get; set; } = [];
    }

    private sealed record ModelOption(string Slug, string DisplayName, string DefaultReasoningLevel, IReadOnlyList<string> ReasoningLevels, int Priority);
    private sealed record UsageSnapshot(IReadOnlyList<UsageWindow> Windows);
    private sealed record UsageWindow(long? DurationMinutes, int UsedPercent, long? ResetsAt);

    private sealed class ChatMessage(bool isUser, string text)
    {
        public bool IsUser { get; } = isUser;
        public string Text { get; set; } = text;
    }
}
