using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly List<ProjectEntry> _removedProjects = [];
    private readonly List<QueuedPrompt> _promptQueue = [];
    private IReadOnlyList<ModelOption> _availableModels = [];
    private Task<IReadOnlyList<ModelOption>>? _modelCatalogTask;
    private readonly Dictionary<string, FrameworkElement> _named = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _reloadDebounce;
    private readonly DispatcherTimer _stateSaveDebounce;
    private readonly DispatcherTimer _usageRefreshTimer;
    private readonly DispatcherTimer _updateCheckTimer;
    private readonly DispatcherTimer _repositoryRefreshTimer;
    private readonly CancellationTokenSource _updateCheckCancellation = new();
    private readonly CancellationTokenSource _repositoryRefreshCancellation = new();
    private bool _isRefreshingUsage;
    private bool _isCheckingUpdates;
    private bool _isInstallingUpdate;
    private bool _isClosing;
    private PreparedAppUpdate? _preparedUpdate;
    private GitRepositorySnapshot? _repositorySnapshot;
    private bool _isRefreshingRepository;
    private bool _repositoryRefreshPending;
    private string _updateProgressText = "Проверка обновлений…";
    private string _usageLimitsText = "Загружаю лимиты Codex…";
    private string _usageLimitsTooltip = "Лимиты аккаунта Codex";
    private FileSystemWatcher? _viewWatcher;
    private Forms.NotifyIcon? _trayIcon;
    private DrawingIcon? _trayImage;
    private readonly Dictionary<string, Process?> _activeProcesses = new(StringComparer.Ordinal);
    private bool _isPumpingPromptQueue;
    private bool _sidebarCollapsed;
    private bool _contextOpen;
    private bool _showArchivedChats;
    private string _projectPath = FindProjectRoot();
    private ProjectEntry? _selectedProject;
    private string? _startupIssue;
    private string _selectedModel = DefaultModel;
    private string _selectedEffort = DefaultEffort;
    private ChatThread? _currentThread;
    private bool HasActiveRuns => _threads.Any(thread => thread.ActiveRun is not null);
    private bool IsCurrentThreadRunning => _currentThread?.ActiveRun is not null;
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
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateCheckTimer.Tick += async (_, _) => await CheckForUpdatesAsync(interactive: false);
        _repositoryRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _repositoryRefreshTimer.Tick += async (_, _) => await RefreshRepositoryInfoAsync();
        LoadState();
        ReloadView();
        InitializeTrayIcon();
        StartViewWatcher();
        _usageRefreshTimer.Start();
        Loaded += (_, _) =>
        {
            _ = RefreshUsageLimitsAsync();
            _ = CheckForUpdatesAsync(interactive: false);
            _updateCheckTimer.Start();
            RenderPromptQueue();
            _ = ResumePendingWorkAsync();
        };
        Closed += (_, _) =>
        {
            _usageRefreshTimer.Stop();
            _updateCheckTimer.Stop();
            _repositoryRefreshTimer.Stop();
            _isClosing = true;
            _updateCheckCancellation.Cancel();
            _updateCheckCancellation.Dispose();
            _repositoryRefreshCancellation.Cancel();
            _repositoryRefreshCancellation.Dispose();
            if (!_isInstallingUpdate && _preparedUpdate is not null)
                AppUpdater.DiscardPreparedUpdate(_preparedUpdate);
            if (_stateSaveDebounce.IsEnabled)
            {
                _stateSaveDebounce.Stop();
            }
            SaveState();
            CleanupWindowResources();
        };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) Hide();
        };
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_isCheckingUpdates || _isInstallingUpdate || _isClosing) return;
        if (_preparedUpdate is not null) return;

        _isCheckingUpdates = true;
        _updateProgressText = "Проверка обновлений…";
        UpdateUpdateButton();
        SetTrayTooltip("Codex — проверяю обновления");
        try
        {
            var update = await AppUpdater.FindUpdateAsync(_updateCheckCancellation.Token);
            if (_isClosing) return;
            if (update is null)
            {
                if (interactive)
                    MessageBox.Show(this, $"Установлена последняя версия CodexGUI ({AppUpdater.CurrentVersion}).", "Обновления", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _updateProgressText = $"Скачивание v{update.Version}…";
            SetTrayTooltip($"Codex — скачиваю обновление v{update.Version}");
            UpdateUpdateButton();
            var preparedUpdate = await AppUpdater.DownloadAndVerifyAsync(update, _updateCheckCancellation.Token);
            if (_isClosing)
            {
                AppUpdater.DiscardPreparedUpdate(preparedUpdate);
                return;
            }

            _preparedUpdate = preparedUpdate;
            UpdateUpdateButton();
            SetTrayTooltip($"Codex — обновление v{update.Version} готово к установке");
            _trayIcon?.ShowBalloonTip(8000, "Обновление скачано", $"Версия {update.Version} готова. Откройте Codex и нажмите кнопку обновления.", Forms.ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception error)
        {
            if (_isClosing) return;
            SetTrayTooltip("Codex — не удалось загрузить обновление");
            _trayIcon?.ShowBalloonTip(5000, "Ошибка обновления", "Не удалось скачать или проверить обновление. Проверка повторится позже.", Forms.ToolTipIcon.Error);
            if (interactive)
                MessageBox.Show(this, $"Не удалось проверить или скачать обновление.\n\n{error.Message}", "Обновление CodexGUI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isCheckingUpdates = false;
            if (!_isClosing && _preparedUpdate is null)
            {
                UpdateUpdateButton();
                SetTrayTooltip("Codex — готов к работе");
            }
        }
    }

    private async Task InstallPreparedUpdateAsync()
    {
        if (_preparedUpdate is not { } preparedUpdate || _isInstallingUpdate || _isClosing) return;
        _isInstallingUpdate = true;
        SetTrayTooltip($"Codex — устанавливаю обновление v{preparedUpdate.Update.Version}");
        UpdateUpdateButton();
        try
        {
            await AppUpdater.InstallPreparedUpdateAsync(preparedUpdate);
            Close();
        }
        catch (Exception error)
        {
            _isInstallingUpdate = false;
            SetTrayTooltip($"Codex — обновление v{preparedUpdate.Update.Version} готово к установке");
            UpdateUpdateButton();
            MessageBox.Show(this, $"Не удалось запустить установку обновления.\n\n{error.Message}", "Обновление CodexGUI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateUpdateButton()
    {
        if (Control<Button>("UpdateButton") is not { } button || Control<TextBlock>("UpdateButtonLabel") is not { } label) return;
        if (_preparedUpdate is { } preparedUpdate)
        {
            label.Text = $"Установить v{preparedUpdate.Update.Version}";
            button.IsEnabled = !_isInstallingUpdate;
            button.ToolTip = "Обновление скачано и проверено. Нажмите, чтобы установить его и перезапустить Codex.";
            button.Background = Brush("#244633");
            button.Foreground = Brush("#B9F4CE");
        }
        else if (_isCheckingUpdates || _isInstallingUpdate)
        {
            label.Text = _isInstallingUpdate ? "Установка…" : _updateProgressText;
            button.IsEnabled = false;
            button.ToolTip = label.Text;
            button.Background = Brush("#202125");
            button.Foreground = Brush("#9A9BA1");
        }
        else
        {
            label.Text = "Проверить обновления";
            button.IsEnabled = true;
            button.ToolTip = $"Текущая версия: {AppUpdater.CurrentVersion}";
            button.Background = Brush("Transparent");
            button.Foreground = Brush("#9A9BA1");
        }
    }

    private void SetTrayTooltip(string text)
    {
        if (_trayIcon is not null) _trayIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private async Task HandleUpdateButtonClickAsync()
    {
        if (_preparedUpdate is not null) await InstallPreparedUpdateAsync();
        else await CheckForUpdatesAsync(interactive: true);
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
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
    }

    internal void ShowFromTray()
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
            if (File.Exists(Path.Combine(directory.FullName, "CodexGUI.csproj"))
                || File.Exists(Path.Combine(directory.FullName, "Views", "WorkspaceView.xaml")))
                return directory.FullName;
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
            var xaml = File.Exists(ViewPath) ? File.ReadAllText(ViewPath) : ReadEmbeddedWorkspaceView();
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

    private static string ReadEmbeddedWorkspaceView()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("CodexGUI.Views.WorkspaceView.xaml")
            ?? throw new FileNotFoundException("Не найдена встроенная разметка WorkspaceView.xaml.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
        Click("RefreshRepositoryButton", (_, _) =>
        {
            RefreshFileTree();
            UpdateChatInfoPanel();
            _ = RefreshRepositoryInfoAsync();
        });
        Click("RepositoryRemoteButton", (_, _) => OpenRepositoryRemote());
        Click("OpenFolderButton", (_, _) => OpenProjectFolder());
        Click("ProjectButton", (_, _) => ChooseProjectFolder());
        Click("AddProjectButton", (_, _) => ShowProjectMenu());
        Click("ArchivedChatsButton", (_, _) => ToggleArchivedChats());
        Click("SendButton", async (_, _) => await SendPromptAsync());
        Click("ClearPromptQueueButton", (_, _) => ClearPromptQueue());
        Click("ModeButton", (_, _) => ShowAccessMenu());
        Click("ModelButton", async (_, _) => await ShowModelMenuAsync());
        Click("EffortButton", async (_, _) => await ShowEffortMenuAsync());
        Click("AddContextButton", (_, _) => AddFileContext());
        Click("ScreenshotButton", async (_, _) => await CaptureScreenshotAsync());
        Click("UpdateButton", async (_, _) => await HandleUpdateButtonClickAsync());

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
        UpdateUpdateButton();
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
        UpdateChatInfoPanel();
        RenderPromptQueue();
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
        if (_contextOpen)
        {
            RefreshFileTree();
            _ = RefreshRepositoryInfoAsync();
        }
        SetStatus(HasActiveRuns ? "Codex работает…" : "Codex CLI готов",
            _startupIssue ?? (HasActiveRuns ? "Есть выполняющиеся запросы." : "Запросы выполняются в выбранной папке."),
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
                Content = thread.ActiveRun is null ? thread.Title : "●  " + thread.Title,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8, 7, 8),
                Margin = new Thickness(0, 1, 0, 1),
                Background = thread == _currentThread ? Brush("#27292E") : Brushes.Transparent,
                Foreground = Brush(thread == _currentThread ? "#F0F0F1" : "#A4A5AA"),
                FontSize = 12,
                ToolTip = thread.ActiveRun is null ? thread.Title : "Выполняется… " + thread.Title
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
        archive.IsEnabled = thread.ActiveRun is null;
        archive.Click += async (_, _) =>
        {
            popup.IsOpen = false;
            await SetThreadArchivedAsync(thread, !thread.IsArchived);
        };
        var delete = CreateThreadActionButton("Удалить чат", "M4,6 H16 M6,6 V17 H14 V6 M7,6 V4 H13 V6 M8,9 V14 M12,9 V14", "#F08C8C");
        delete.IsEnabled = thread.ActiveRun is null;
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
        if (thread.ActiveRun is not null) return;
        try
        {
            if (thread.CodexSessionId is { Length: > 0 } sessionId)
                await CodexCliService.RunSessionCommandAsync(archived ? "archive" : "unarchive", sessionId);
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
        if (thread.ActiveRun is not null) return;
        var result = MessageBox.Show(this,
            $"Удалить чат «{thread.Title}»? Его история будет удалена из Codex CLI и приложения. Это действие нельзя отменить.",
            "Удалить чат", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (thread.CodexSessionId is { Length: > 0 } sessionId)
                await CodexCliService.RunSessionCommandAsync("delete", sessionId);
            _threads.Remove(thread);
            _promptQueue.RemoveAll(prompt => prompt.ThreadId == thread.Id);
            if (_currentThread == thread)
                _currentThread = _threads.LastOrDefault(item => item.ProjectId == thread.ProjectId && item.IsArchived == _showArchivedChats);
            SaveState();
            RenderPromptQueue();
            UpdateChatSectionHeading();
            RefreshChatSelection();
            SetStatus("Чат удален", thread.Title);
        }
        catch (Exception error)
        {
            SetStatus("Не удалось удалить чат", error.Message, isError: true);
        }
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
        var result = await new CodexAppServerClient(CodexCliService.ResolveExecutable()).ReadRateLimitsAsync();
        return ParseUsageSnapshot(result);
    }

    private Task RestoreThreadHistoryAsync(ChatThread thread, bool force = false)
    {
        if (!force && thread.Messages.Count > 0)
        {
            thread.HistoryLoaded = true;
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(thread.CodexSessionId) || (!force && thread.HistoryLoaded))
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
        return await new CodexAppServerClient(CodexCliService.ResolveExecutable()).ReadThreadAsync(sessionId);
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
                    default:
                        if (CreateActivityMessage(item, true) is { } activity) messages.Add(activity);
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
        if (_currentThread is not null)
        {
            _ = RestoreThreadContextUsageAsync(_currentThread);
            _ = RestoreThreadHistoryAsync(_currentThread);
        }
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
            var projectMenu = new ContextMenu();
            var removeProjectItem = new MenuItem { Header = "Убрать из списка…" };
            removeProjectItem.Click += (_, _) => RemoveProjectFromList(project);
            projectMenu.Items.Add(removeProjectItem);
            button.ContextMenu = projectMenu;
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
            var welcome = new StackPanel { Margin = new Thickness(0, 104, 0, 0), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Center };
            welcome.Children.Add(new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(14),
                Background = Brush("#24262B"),
                BorderBrush = Brush("#34363C"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 20),
                Child = new TextBlock { Text = "✳", FontSize = 25, Foreground = Brush("#E5E5E7"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            welcome.Children.Add(new TextBlock { Text = _currentThread?.Title ?? $"Работаем над проектом «{_selectedProject?.Name ?? "CodexGUI"}»", FontSize = 29, FontWeight = FontWeights.SemiBold, Foreground = Brush("#F0F0F1"), TextWrapping = TextWrapping.Wrap, LineHeight = 37 });
            var detail = _currentThread?.HistoryLoading == true
                ? "Загружаю историю из Codex…"
                : _currentThread?.HistoryError is { } historyError
                    ? $"Не удалось восстановить историю: {historyError}"
                    : _currentThread?.CodexSessionId is not null
                ? "Этот чат сохранён в Codex. Отправьте сообщение, чтобы продолжить разговор."
                    : "Опишите задачу простыми словами. Codex изучит проект, объяснит код или поможет внести изменения.";
            welcome.Children.Add(new TextBlock { Text = detail, FontSize = 14, LineHeight = 22, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#A1A2A8"), Margin = new Thickness(0, 11, 0, 24), MaxWidth = 600 });
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
            Content = new Grid
            {
                Children =
                {
                    new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "↗", Foreground = Brush("#777981"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }
                }
            },
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brush("#1A1B1F"),
            Foreground = Brush("#D1D2D5"),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 4, 9, 4),
            Width = 330,
            BorderBrush = Brush("#303238"),
            BorderThickness = new Thickness(1)
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
        if (message.ActivityTitle is { } activityTitle)
        {
            var details = new TextBox
            {
                Text = message.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent, Foreground = Brush("#AFB5C0"),
                BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas"),
                FontSize = 12, Margin = new Thickness(0, 8, 0, 0),
                MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var expander = new Expander
            {
                Header = activityTitle, Content = details, IsExpanded = message.IsExpanded,
                Foreground = Brush(message.IsError ? "#F09999" : "#9FAFC7"), FontSize = 12
            };
            expander.Expanded += (_, _) => message.IsExpanded = true;
            expander.Collapsed += (_, _) => message.IsExpanded = false;
            return new Border
            {
                Background = Brush("#1D2026"), BorderBrush = Brush(message.IsError ? "#78434A" : "#394355"),
                BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(4, 4, 4, 10), Child = expander
            };
        }
        var outer = new StackPanel
        {
            HorizontalAlignment = message.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 700,
            Margin = new Thickness(0, 18, 0, 22)
        };
        if (!message.IsUser)
            outer.Children.Add(new TextBlock { Text = "✳  Codex", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("#C4C5C9"), Margin = new Thickness(1, 0, 0, 8) });

        var text = new TextBlock
        {
            Text = message.Text,
            FontSize = 14,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(message.IsUser ? "#E6E6E8" : "#D2D3D6")
        };
        if (!message.IsUser && IsCurrentThreadRunning && ReferenceEquals(_currentThread?.Messages.LastOrDefault(), message))
            _streamingAssistantTextBlock = text;
        if (message.IsUser)
        {
            var bubble = new Border { Background = Brush("#292B30"), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 10), Child = text };
            outer.Children.Add(bubble);
        }
        else outer.Children.Add(new Border
        {
            Background = Brush("#202227"), BorderBrush = Brush("#34373F"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 13, 16, 13), Child = text
        });
        return outer;
    }

    private static void UpsertTimelineItem(List<ChatMessage> messages, ChatMessage anchor,
        Dictionary<string, ChatMessage> items, string? id, ChatMessage message)
    {
        if (id is not null && items.TryGetValue(id, out var existing))
        {
            existing.Text = message.Text;
            existing.ActivityTitle = message.ActivityTitle;
            existing.IsError = message.IsError;
            return;
        }
        var index = messages.IndexOf(anchor);
        if (index < 0) return;
        messages.Insert(index, message);
        if (id is not null) items[id] = message;
    }

    private static ChatMessage? CreateActivityMessage(JsonElement item, bool completed)
    {
        var type = GetString(item, "type");
        var title = type switch
        {
            "reasoning" => "Размышления",
            "command_execution" or "commandExecution" => "Команда",
            "file_change" or "fileChange" => "Изменение файлов",
            "mcp_tool_call" or "mcpToolCall" => "Вызов инструмента",
            "web_search" or "webSearch" => "Поиск в интернете",
            "todo_list" or "plan" => "План работы",
            "error" => "Ошибка",
            _ => null
        };
        if (title is null) return null;
        var failed = type == "error" || GetString(item, "status") is "failed" or "declined" ||
            (item.TryGetProperty("exit_code", out var exit) && exit.TryGetInt32(out var code) && code != 0);
        var parts = new List<string>();
        foreach (var key in new[] { "command", "text", "query", "server", "tool", "aggregated_output", "aggregatedOutput" })
            if (GetString(item, key) is { Length: > 0 } value) parts.Add(value);
        foreach (var key in new[] { "summary", "content", "changes", "items", "arguments", "result", "error" })
            if (item.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null)
                parts.Add(value.ToString());
        return new ChatMessage(false, string.Join(Environment.NewLine + Environment.NewLine, parts))
        {
            ActivityTitle = $"{(failed ? "✕" : completed ? "✓" : "●")}  {title} · {(failed ? "ошибка" : completed ? "завершено" : "выполняется")}",
            IsError = failed
        };
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

    private void UpdateChatInfoPanel()
    {
        if (Control<TextBlock>("ChatInfoTitle") is not { } title ||
            Control<TextBlock>("ChatInfoDetails") is not { } details ||
            Control<TextBlock>("ChatSessionInfo") is not { } session) return;

        var thread = _currentThread;
        title.Text = thread?.Title ?? "Новый чат";
        var modelName = _availableModels.FirstOrDefault(model => model.Slug == _selectedModel)?.DisplayName ?? _selectedModel;
        details.Text = $"{modelName} · Сообщений: {thread?.Messages.Count(message => message.ActivityTitle is null) ?? 0}";
        if (thread?.ContextTokens is { } tokens && thread.ContextWindowTokens is > 0 and { } contextWindow)
            details.Text += $" · Контекст: {tokens * 100 / contextWindow}%";
        session.Text = thread?.CodexSessionId is { Length: > 0 } sessionId
            ? $"Сессия {sessionId[..Math.Min(8, sessionId.Length)]}"
            : "Сессия создастся при первом запросе";
    }

    private async Task RefreshRepositoryInfoAsync()
    {
        if (!_contextOpen || _isClosing) return;
        if (_isRefreshingRepository)
        {
            _repositoryRefreshPending = true;
            return;
        }

        _isRefreshingRepository = true;
        try
        {
            do
            {
                _repositoryRefreshPending = false;
                var projectPath = _projectPath;
                var snapshot = await GitRepositoryInspector.ReadAsync(projectPath, _repositoryRefreshCancellation.Token);
                if (_isClosing || !_contextOpen) return;
                if (!string.Equals(projectPath, _projectPath, StringComparison.OrdinalIgnoreCase))
                {
                    _repositoryRefreshPending = true;
                    continue;
                }

                _repositorySnapshot = snapshot;
                RenderRepositoryInfo(snapshot);
            }
            while (_repositoryRefreshPending && !_isClosing && _contextOpen);
        }
        catch (OperationCanceledException) when (_isClosing) { }
        finally
        {
            _isRefreshingRepository = false;
            if (_repositoryRefreshPending && !_isClosing && _contextOpen)
                _ = RefreshRepositoryInfoAsync();
        }
    }

    private void RenderRepositoryInfo(GitRepositorySnapshot snapshot)
    {
        if (Control<TextBlock>("RepositoryBranch") is not { } branch ||
            Control<TextBlock>("RepositorySummary") is not { } summary ||
            Control<StackPanel>("RepositoryChangesPanel") is not { } changesPanel ||
            Control<Button>("RepositoryRemoteButton") is not { } remoteButton) return;

        changesPanel.Children.Clear();
        remoteButton.Visibility = snapshot.RemoteUrl is null ? Visibility.Collapsed : Visibility.Visible;
        remoteButton.ToolTip = snapshot.RemoteUrl;
        if (!snapshot.IsRepository)
        {
            branch.Text = snapshot.Error ?? "Git-репозиторий не найден";
            summary.Text = "Нет данных о состоянии репозитория";
            return;
        }

        var branchLabel = string.IsNullOrWhiteSpace(snapshot.Branch) ? "(без ветки)" : snapshot.Branch;
        if (snapshot.Upstream is { Length: > 0 } upstream) branchLabel += $" · {upstream}";
        if (snapshot.Ahead > 0 || snapshot.Behind > 0)
            branchLabel += $" · ↑{snapshot.Ahead} ↓{snapshot.Behind}";
        branch.Text = branchLabel;

        var staged = snapshot.Changes.Where(file => !file.IsUntracked && file.IndexStatus != '.').ToArray();
        var modified = snapshot.Changes.Where(file => !file.IsUntracked && file.WorktreeStatus != '.').ToArray();
        var untracked = snapshot.Changes.Where(file => file.IsUntracked).ToArray();
        summary.Text = snapshot.Changes.Count == 0
            ? "Нет незакоммиченных изменений"
            : $"Файлов: {snapshot.Changes.Count} · В индексе: {staged.Length} · Изменены: {modified.Length} · Новые: {untracked.Length}";

        AddRepositoryFileGroup(changesPanel, "В ИНДЕКСЕ", staged, snapshot.Root);
        AddRepositoryFileGroup(changesPanel, "ИЗМЕНЕНЫ", modified, snapshot.Root);
        AddRepositoryFileGroup(changesPanel, "НЕ ОТСЛЕЖИВАЮТСЯ", untracked, snapshot.Root);
        if (snapshot.Changes.Count == 0)
            changesPanel.Children.Add(new TextBlock { Text = "Рабочее дерево чистое", FontSize = 10, Foreground = Brush("#777980"), Margin = new Thickness(0, 5, 0, 2) });
    }

    private void AddRepositoryFileGroup(StackPanel panel, string heading, IReadOnlyList<RepositoryFileStatus> files, string repositoryRoot)
    {
        if (files.Count == 0) return;
        panel.Children.Add(new TextBlock
        {
            Text = $"{heading} · {files.Count}",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#777980"),
            Margin = new Thickness(0, 8, 0, 2)
        });

        foreach (var file in files.Take(8))
        {
            var status = file.IsUntracked ? "?" : $"{file.IndexStatus}{file.WorktreeStatus}";
            var button = new Button
            {
                Content = new TextBlock { Text = $"{status}  {file.Path}", FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(2, 2, 0, 2),
                Background = Brushes.Transparent,
                Foreground = Brush("#AEB0B5"),
                ToolTip = $"Открыть {file.Path} в проводнике"
            };
            button.Click += (_, _) => OpenRepositoryFile(repositoryRoot, file.Path);
            panel.Children.Add(button);
        }

        if (files.Count > 8)
            panel.Children.Add(new TextBlock { Text = $"и ещё {files.Count - 8}…", FontSize = 10, Foreground = Brush("#777980"), Margin = new Thickness(2, 2, 0, 0) });
    }

    private void OpenRepositoryFile(string repositoryRoot, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return;

        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add(File.Exists(fullPath) ? $"/select,{fullPath}" : repositoryRoot);
        _ = Process.Start(startInfo);
    }

    private void OpenRepositoryRemote()
    {
        if (_repositorySnapshot?.RemoteUrl is not { } remoteUrl) return;
        _ = Process.Start(new ProcessStartInfo(remoteUrl) { UseShellExecute = true });
    }

    private void ScheduleStateSave()
    {
        _stateSaveDebounce.Stop();
        _stateSaveDebounce.Start();
    }

    private void RenderPromptQueue()
    {
        if (Control<Border>("PromptQueueBorder") is not { } border ||
            Control<StackPanel>("PromptQueueList") is not { } list) return;

        list.Children.Clear();
        border.Visibility = _promptQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Control<TextBlock>("PromptQueueLabel") is { } heading)
            heading.Text = $"В ОЧЕРЕДИ · {_promptQueue.Count}";
        if (Control<Button>("ClearPromptQueueButton") is { } clearButton)
            clearButton.IsEnabled = _promptQueue.Count > 0;

        foreach (var queued in _promptQueue)
        {
            var threadTitle = _threads.FirstOrDefault(thread => thread.Id == queued.ThreadId)?.Title;
            var text = queued.Text.ReplaceLineEndings(" ").Trim();
            if (text.Length > 100) text = text[..97] + "…";
            if (_currentThread?.Id != queued.ThreadId && !string.IsNullOrWhiteSpace(threadTitle))
                text = $"{threadTitle}: {text}";

            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = Brush("#C7C8CC"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = queued.Text
            });
            var remove = new Button
            {
                Content = "×",
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                Foreground = Brush("#9A9BA1"),
                ToolTip = "Убрать из очереди"
            };
            remove.Click += (_, _) => RemovePromptFromQueue(queued.Id);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            list.Children.Add(row);
        }

        UpdateSendButtonState();
    }

    private void UpdateSendButtonState()
    {
        var queueing = IsCurrentThreadRunning || _promptQueue.Any(prompt => prompt.ThreadId == _currentThread?.Id) || _isPumpingPromptQueue;
        if (Control<Button>("SendButton") is { } send)
        {
            send.IsEnabled = !_isInstallingUpdate && !_isClosing;
            send.ToolTip = queueing ? "Добавить в очередь (Enter)" : "Отправить (Enter)";
        }
        if (Control<TextBlock>("SendButtonIcon") is { } icon)
            icon.Text = queueing ? "＋" : "↑";
    }

    private void RemovePromptFromQueue(string promptId)
    {
        if (_promptQueue.RemoveAll(prompt => prompt.Id == promptId) == 0) return;
        SaveState();
        RenderPromptQueue();
    }

    private void ClearPromptQueue()
    {
        if (_promptQueue.Count == 0) return;
        var result = MessageBox.Show(this,
            $"Убрать из очереди все {_promptQueue.Count} сообщений?",
            "Очередь сообщений",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        _promptQueue.Clear();
        SaveState();
        RenderPromptQueue();
    }

    private async Task SendPromptAsync()
    {
        if (_isClosing || Control<TextBox>("PromptInput") is not { } input) return;
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

        _currentThread ??= NewThread();
        var thread = _currentThread;
        if (thread.Messages.Count == 0 && thread.ActiveRun is null && !_promptQueue.Any(item => item.ThreadId == thread.Id))
        {
            thread.Title = TrimTitle(prompt);
            RenderThreads();
        }
        var project = _projects.FirstOrDefault(item => item.Id == thread.ProjectId);
        _promptQueue.Add(new QueuedPrompt
        {
            ThreadId = thread.Id,
            Text = prompt,
            Model = _selectedModel,
            Effort = _selectedEffort,
            FullAccess = project?.FullAccess == true
        });
        SaveState();
        input.Clear();
        RenderPromptQueue();
        await ProcessQueuedPromptsAsync();
    }

    private async Task ResumePendingWorkAsync()
    {
        foreach (var thread in _threads.Where(item => item.ActiveRun is not null).ToList())
        {
            if (thread.ActiveRun is not { } activeRun) continue;
            var assistantMessage = EnsureActiveRunMessages(thread, activeRun);
            _ = ExecuteActiveCliRunAsync(thread, activeRun, assistantMessage, reloadHistoryWhenFinished: true);
        }
        if (_currentThread is not null)
        {
            _ = RestoreThreadContextUsageAsync(_currentThread);
            await RestoreThreadHistoryAsync(_currentThread);
        }
        RenderThreads();
        RenderConversation();
        await ProcessQueuedPromptsAsync();
    }

    private static ChatMessage EnsureActiveRunMessages(ChatThread thread, ActiveCliRun activeRun)
    {
        var userIndex = thread.Messages.FindLastIndex(message => message.IsUser && message.Text == activeRun.Prompt);
        if (userIndex < 0)
        {
            thread.Messages.Add(new ChatMessage(true, activeRun.Prompt));
            userIndex = thread.Messages.Count - 1;
        }

        // The worker log is replayed from the start, including messages already in history.
        // Rebuild only this active turn to avoid duplicating its messages and activities.
        thread.Messages.RemoveRange(userIndex + 1, thread.Messages.Count - userIndex - 1);
        var assistant = new ChatMessage(false, "CLI продолжает выполнять запрос…");
        thread.Messages.Add(assistant);
        return assistant;
    }

    private async Task ProcessQueuedPromptsAsync()
    {
        if (_isPumpingPromptQueue || _isClosing) return;
        _isPumpingPromptQueue = true;
        try
        {
            while (!_isClosing)
            {
                var missing = _promptQueue.RemoveAll(prompt => _threads.All(thread => thread.Id != prompt.ThreadId));
                if (missing > 0) SaveState();
                var queued = _promptQueue.FirstOrDefault(prompt =>
                    _threads.FirstOrDefault(thread => thread.Id == prompt.ThreadId) is { ActiveRun: null } thread &&
                    Directory.Exists(thread.ProjectPath));
                if (queued is null)
                {
                    var unavailable = _promptQueue.FirstOrDefault(prompt =>
                        _threads.FirstOrDefault(thread => thread.Id == prompt.ThreadId) is { ActiveRun: null } thread &&
                        !Directory.Exists(thread.ProjectPath));
                    if (unavailable is not null && _currentThread?.Id == unavailable.ThreadId)
                        SetStatus("Папка проекта недоступна", _currentThread.ProjectPath, isError: true);
                    break;
                }
                var thread = _threads.First(item => item.Id == queued.ThreadId);
                if (thread.IsArchived)
                {
                    await SetThreadArchivedAsync(thread, archived: false);
                    if (thread.IsArchived) continue;
                }
                var activeRun = CreateActiveCliRun(queued);
                thread.ActiveRun = activeRun;
                _promptQueue.Remove(queued);
                SaveState();
                RenderThreads();
                UpdateSendButtonState();
                if (thread.CodexSessionId is not null && thread.Messages.Count == 0)
                {
                    await RestoreThreadHistoryAsync(thread);
                    if (!thread.HistoryLoaded)
                    {
                        thread.ActiveRun = null;
                        _promptQueue.Add(queued);
                        SaveState();
                        RenderThreads();
                        RenderPromptQueue();
                        if (_currentThread == thread)
                            SetStatus("Не удалось восстановить чат", thread.HistoryError ?? thread.Title, isError: true);
                        return;
                    }
                }
                var userMessage = new ChatMessage(true, queued.Text);
                var assistantMessage = new ChatMessage(false, "Думаю…");
                thread.Messages.Add(userMessage);
                thread.Messages.Add(assistantMessage);
                SaveState();
                if (_currentThread == thread)
                {
                    RenderConversation();
                    UpdateChatInfoPanel();
                }
                _ = ExecuteActiveCliRunAsync(thread, activeRun, assistantMessage, reloadHistoryWhenFinished: false);
            }
        }
        finally
        {
            _isPumpingPromptQueue = false;
            UpdateSendButtonState();
        }
    }

    private async Task ExecuteActiveCliRunAsync(ChatThread thread, ActiveCliRun activeRun, ChatMessage assistantMessage, bool reloadHistoryWhenFinished)
    {
        try
        {
            var response = await RunCodexAsync(thread, activeRun, assistantMessage);
            assistantMessage.Text = string.IsNullOrWhiteSpace(response)
                ? "Codex завершил запрос без текстового ответа. Проверьте детали команды в терминале."
                : response;
            if (assistantMessage.HasCompletedMessages && string.IsNullOrWhiteSpace(response))
                thread.Messages.Remove(assistantMessage);
            if (_currentThread == thread) SetStatus("Codex CLI готов", "Ответ получен.");
        }
        catch (Exception error)
        {
            assistantMessage.Text = "Не удалось выполнить запрос:\n" + error.Message;
            if (_currentThread == thread) SetStatus("Ошибка запроса", error.Message, isError: true);
        }
        finally
        {
            if (_activeProcesses.Remove(activeRun.RunId, out var activeProcess)) activeProcess?.Dispose();
            if (thread.ActiveRun?.RunId == activeRun.RunId) thread.ActiveRun = null;
            SaveState();
            if (reloadHistoryWhenFinished && thread.CodexSessionId is not null)
                await RestoreThreadHistoryAsync(thread, force: true);
            CleanupCliRun(activeRun);
            RenderThreads();
            if (_currentThread == thread) RenderConversation();
            UpdateChatInfoPanel();
            RenderPromptQueue();
            UpdateSendButtonState();
            _ = RefreshUsageLimitsAsync();
            if (_contextOpen) await RefreshRepositoryInfoAsync();
            await ProcessQueuedPromptsAsync();
        }
    }

    private ActiveCliRun CreateActiveCliRun(QueuedPrompt queued)
    {
        var runId = Guid.NewGuid().ToString("N");
        var runDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "workers", runId);
        Directory.CreateDirectory(runDirectory);
        return new ActiveCliRun
        {
            RunId = runId,
            Prompt = queued.Text,
            Model = queued.Model,
            Effort = queued.Effort,
            FullAccess = queued.FullAccess,
            RequestPath = Path.Combine(runDirectory, "request.json"),
            ScriptPath = Path.Combine(runDirectory, "CodexCliWorker.ps1"),
            StartedPath = Path.Combine(runDirectory, "started.json"),
            OutputPath = Path.Combine(runDirectory, "stdout.jsonl"),
            ErrorPath = Path.Combine(runDirectory, "stderr.txt"),
            CompletionPath = Path.Combine(runDirectory, "completed.json")
        };
    }

    private async Task<string> RunCodexAsync(ChatThread thread, ActiveCliRun activeRun, ChatMessage assistantMessage)
    {
        var process = await StartOrAttachCliWorkerAsync(thread, activeRun);
        _activeProcesses[activeRun.RunId] = process;
        var response = new StringBuilder();
        var responseLock = new object();
        var completedItems = new Dictionary<string, ChatMessage>();
        var updateQueued = 0;
        var outputTail = new CliOutputTail();
        var missingCompletionChecks = 0;

        void QueueResponseRefresh()
        {
            if (_isClosing || Dispatcher.HasShutdownStarted) return;
            if (Interlocked.Exchange(ref updateQueued, 1) != 0) return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref updateQueued, 0);
                if (_isClosing) return;
                string text;
                lock (responseLock) text = response.ToString();
                if (thread.ActiveRun?.RunId != activeRun.RunId) return;
                if (thread.Messages.Count == 0 || !ReferenceEquals(thread.Messages[^1], assistantMessage)) return;
                assistantMessage.Text = string.IsNullOrEmpty(text) ? "Думаю…" : text;
                if (_currentThread == thread && _streamingAssistantTextBlock is { } messageText)
                {
                    messageText.Text = assistantMessage.Text;
                    if (Control<ScrollViewer>("ConversationScroll") is { } scroll)
                        scroll.ScrollToEnd();
                }
            }, DispatcherPriority.Background);
        }

        void AppendResponse(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (responseLock)
            {
                response.Append(text);
            }
            QueueResponseRefresh();
        }

        void HandleOutputLine(string line)
        {
            if (_isClosing || string.IsNullOrWhiteSpace(line)) return;
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
                        if (_currentThread == thread) UpdateChatInfoPanel();
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
                        if (_currentThread == thread) UpdateChatInfoPanel();
                    }, DispatcherPriority.Background);
                }

                if (eventType is "item.started" or "item.updated" or "item.completed" && root.TryGetProperty("item", out var item))
                {
                    var completed = eventType == "item.completed";
                    var isAgent = GetString(item, "type") == "agent_message";
                    var message = isAgent
                        ? new ChatMessage(false, GetString(item, "text") ?? "")
                        : CreateActivityMessage(item, completed);
                    if (message is null || (isAgent && !completed)) return;
                    var itemId = GetString(item, "id");
                    ApplyItemUpdate(message, itemId, isAgent);
                }
                else if (eventType == "response.output_text.delta" && GetString(root, "delta") is { } delta)
                    AppendResponse(delta);
            }
            catch (JsonException)
            {
                AppendResponse(line + Environment.NewLine);
            }
        }

        void ApplyItemUpdate(ChatMessage message, string? itemId, bool isAgent)
        {
            // Apply in stream order on the UI thread, before consuming the next event.
            Dispatcher.Invoke(() =>
            {
                if (thread.ActiveRun?.RunId != activeRun.RunId) return;
                UpsertTimelineItem(thread.Messages, assistantMessage, completedItems, itemId, message);
                if (isAgent)
                {
                    assistantMessage.HasCompletedMessages = true;
                    lock (responseLock) response.Clear();
                }
                assistantMessage.Text = "Codex работает…";
                if (_currentThread == thread) RenderConversation();
            });
        }

        while (true)
        {
            foreach (var line in await ReadAppendedOutputLinesAsync(activeRun.OutputPath, outputTail))
                HandleOutputLine(line);

            var workerExited = process is null || process.HasExited;
            if (workerExited && File.Exists(activeRun.CompletionPath))
            {
                await Task.Delay(100);
                foreach (var line in await ReadAppendedOutputLinesAsync(activeRun.OutputPath, outputTail))
                    HandleOutputLine(line);
                if (outputTail.PendingBytes.Count > 0)
                {
                    HandleOutputLine(Encoding.UTF8.GetString(outputTail.PendingBytes.ToArray()).TrimEnd('\r'));
                    outputTail.PendingBytes.Clear();
                }
                break;
            }
            if (workerExited)
            {
                var replacement = TryGetCliWorkerProcess(activeRun);
                if (replacement is not null && replacement.Id != process?.Id)
                {
                    process?.Dispose();
                    process = replacement;
                    _activeProcesses[activeRun.RunId] = replacement;
                    continue;
                }
                if (File.Exists(activeRun.CompletionPath)) continue;
                if (++missingCompletionChecks > 20)
                    throw new InvalidOperationException("Фоновый процесс Codex завершился без файла результата.");
                await Task.Delay(100);
                continue;
            }

            await Task.Delay(100);
        }

        var completion = JsonSerializer.Deserialize<CodexCliWorkerCompletion>(await File.ReadAllTextAsync(activeRun.CompletionPath));
        if (completion?.SessionId is { Length: > 0 } sessionId && thread.CodexSessionId != sessionId)
        {
            thread.CodexSessionId = sessionId;
            SaveState();
        }
        if (completion is null || completion.ExitCode != 0)
        {
            var detail = File.Exists(activeRun.ErrorPath) ? (await File.ReadAllTextAsync(activeRun.ErrorPath)).Trim() : "";
            if (string.IsNullOrWhiteSpace(detail)) detail = completion?.Error ?? response.ToString().Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"codex завершился с кодом {completion?.ExitCode ?? -1}."
                : detail);
        }

        lock (responseLock) return response.ToString().Trim();
    }

    private async Task<Process?> StartOrAttachCliWorkerAsync(ChatThread thread, ActiveCliRun run)
    {
        if (TryGetCliWorkerProcess(run) is { } existing) return existing;
        if (File.Exists(run.CompletionPath)) return null;
        if (File.Exists(run.StartedPath))
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100);
                if (TryGetCliWorkerProcess(run) is { } delayed) return delayed;
                if (File.Exists(run.CompletionPath)) return null;
            }
            throw new InvalidOperationException("Фоновый процесс Codex завершился до того, как интерфейс смог подключиться.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(run.RequestPath)!);
        using var workerResource = typeof(MainWindow).Assembly.GetManifestResourceStream("CodexGUI.CodexCliWorker.ps1")
            ?? throw new FileNotFoundException("Не найден сценарий фонового запуска Codex CLI.");
        await using (var workerFile = File.Create(run.ScriptPath))
            await workerResource.CopyToAsync(workerFile);

        var request = new CodexCliWorkerRequest
        {
            RunId = run.RunId,
            ExecutablePath = CodexCliService.ResolveExecutable(),
            WorkingDirectory = thread.ProjectPath,
            Arguments = BuildCodexArguments(thread, run),
            StartedPath = run.StartedPath,
            OutputPath = run.OutputPath,
            ErrorPath = run.ErrorPath,
            CompletionPath = run.CompletionPath
        };
        await File.WriteAllTextAsync(run.RequestPath, JsonSerializer.Serialize(request));

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(run.ScriptPath)!
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(run.ScriptPath);
        startInfo.ArgumentList.Add("-RequestPath");
        startInfo.ArgumentList.Add(run.RequestPath);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить фоновый процесс Codex.");
        run.WorkerProcessId = process.Id;
        run.WorkerStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        SaveState();
        return process;
    }

    private static List<string> BuildCodexArguments(ChatThread thread, ActiveCliRun run)
    {
        var arguments = new List<string>();
        if (run.FullAccess)
        {
            arguments.Add("--dangerously-bypass-approvals-and-sandbox");
        }
        else
        {
            arguments.Add("--ask-for-approval");
            arguments.Add("on-request");
        }
        arguments.Add("--model");
        arguments.Add(run.Model);
        arguments.Add("--config");
        arguments.Add($"model_reasoning_effort={run.Effort}");
        arguments.Add("exec");
        if (thread.CodexSessionId is not null)
        {
            arguments.Add("resume");
            arguments.Add("--json");
            arguments.Add(thread.CodexSessionId);
            arguments.Add(run.Prompt);
        }
        else
        {
            arguments.Add("--json");
            arguments.Add("--skip-git-repo-check");
            if (!run.FullAccess)
            {
                arguments.Add("--sandbox");
                arguments.Add("workspace-write");
            }
            arguments.Add("-C");
            arguments.Add(thread.ProjectPath);
            arguments.Add(run.Prompt);
        }
        return arguments;
    }

    private static Process? TryGetCliWorkerProcess(ActiveCliRun run)
    {
        if (File.Exists(run.StartedPath))
        {
            try
            {
                using var marker = JsonDocument.Parse(File.ReadAllText(run.StartedPath));
                var root = marker.RootElement;
                if (GetString(root, "RunId") == run.RunId &&
                    root.TryGetProperty("ProcessId", out var pid) && pid.TryGetInt32(out var processId) &&
                    root.TryGetProperty("StartTimeUtcTicks", out var ticks) && ticks.TryGetInt64(out var startTicks))
                {
                    run.WorkerProcessId = processId;
                    run.WorkerStartTimeUtcTicks = startTicks;
                }
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        }

        if (run.WorkerProcessId <= 0) return null;
        try
        {
            var process = Process.GetProcessById(run.WorkerProcessId);
            var actualStart = process.StartTime.ToUniversalTime().Ticks;
            if (run.WorkerStartTimeUtcTicks > 0 && Math.Abs(actualStart - run.WorkerStartTimeUtcTicks) > TimeSpan.TicksPerSecond * 2)
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadAppendedOutputLinesAsync(string path, CliOutputTail tail)
    {
        if (!File.Exists(path)) return [];
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= tail.Offset) return [];
            stream.Position = tail.Offset;
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory());
                if (count <= 0) break;
                tail.Offset += count;
                for (var index = 0; index < count; index++) tail.PendingBytes.Add(buffer[index]);
            }

            var lines = new List<string>();
            var lineStart = 0;
            for (var index = 0; index < tail.PendingBytes.Count; index++)
            {
                if (tail.PendingBytes[index] != (byte)'\n') continue;
                var length = index - lineStart;
                if (length > 0 && tail.PendingBytes[index - 1] == (byte)'\r') length--;
                lines.Add(Encoding.UTF8.GetString(tail.PendingBytes.GetRange(lineStart, length).ToArray()));
                lineStart = index + 1;
            }
            if (lineStart > 0) tail.PendingBytes.RemoveRange(0, lineStart);
            return lines;
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
        {
            return [];
        }
    }

    private static void CleanupCliRun(ActiveCliRun run)
    {
        try { Directory.Delete(Path.GetDirectoryName(run.RequestPath)!, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
            label.Text = "100%";
            label.ToolTip = _currentThread?.CodexSessionId is null
                ? "Новый чат · контекст пока не использован"
                : "Загружаю данные окна контекста для этой сессии…";
            arc.Data = Geometry.Empty;
            arc.Stroke = Brush("#77C99B");
            return;
        }

        var availableTokens = Math.Max(window.Value - tokens.Value, 0);
        var usedFraction = Math.Clamp((double)Math.Min(tokens.Value, window.Value) / window.Value, 0d, 1d);
        var usedPercent = (int)Math.Round(usedFraction * 100, MidpointRounding.AwayFromZero);
        label.Text = usedPercent.ToString(CultureInfo.InvariantCulture) + "%";
        label.ToolTip = $"Context window:\n{usedPercent}% used ({100 - usedPercent}% left)\n{tokens.Value.ToString("N0", CultureInfo.CurrentCulture)} tokens used\n{availableTokens.ToString("N0", CultureInfo.CurrentCulture)} tokens available";
        arc.Stroke = Brush(usedPercent >= 85 ? "#E78C8C" : usedPercent >= 65 ? "#E6BC70" : "#77C99B");

        const double center = 18;
        const double radius = 14;
        if (usedFraction <= 0)
        {
            arc.Data = Geometry.Empty;
            return;
        }
        if (usedFraction >= 1)
        {
            arc.Data = new EllipseGeometry(new Point(center, center), radius, radius);
            return;
        }

        var angle = usedFraction * Math.PI * 2;
        var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(center, center - radius), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, usedFraction > 0.5, SweepDirection.Clockwise, true));
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
        _ = RestoreThreadContextUsageAsync(thread);
        _ = RestoreThreadHistoryAsync(thread);
    }

    private async Task RestoreThreadContextUsageAsync(ChatThread thread)
    {
        if (thread.ContextTokens is not null || string.IsNullOrWhiteSpace(thread.CodexSessionId)) return;

        try
        {
            var usage = await ReadThreadContextUsageAsync(thread.CodexSessionId);
            if (usage is null) return;
            thread.ContextTokens = usage.Value.Tokens;
            thread.ContextWindowTokens = usage.Value.Window;
            ScheduleStateSave();
            if (_currentThread == thread) UpdateContextIndicator();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // The indicator remains unavailable when this session has no readable usage event.
        }
    }

    private static Task<(long Tokens, long Window)?> ReadThreadContextUsageAsync(string sessionId)
    {
        var sessionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (!Directory.Exists(sessionsRoot))
            return Task.FromResult<(long Tokens, long Window)?>(null);

        var files = Directory.EnumerateFiles(sessionsRoot, $"rollout-*-{sessionId}.jsonl", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            using var reader = new StreamReader(file);
            string? line;
            var isSession = false;
            (long Tokens, long Window)? latestUsage = null;
            while ((line = reader.ReadLine()) is not null)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (GetString(root, "type") == "session_meta" && root.TryGetProperty("payload", out var metadata))
                    isSession = GetString(metadata, "session_id") == sessionId;

                if (!isSession || GetString(root, "type") != "event_msg" ||
                    !root.TryGetProperty("payload", out var payload) || GetString(payload, "type") != "token_count" ||
                    !payload.TryGetProperty("info", out var info) ||
                    !info.TryGetProperty("last_token_usage", out var usage) ||
                    !usage.TryGetProperty("total_tokens", out var tokens) || !tokens.TryGetInt64(out var tokenCount) || tokenCount < 0 ||
                    !info.TryGetProperty("model_context_window", out var window) || !window.TryGetInt64(out var windowSize) || windowSize <= 0)
                    continue;

                latestUsage = (tokenCount, windowSize);
            }

            if (isSession && latestUsage is not null) return Task.FromResult(latestUsage);
        }

        return Task.FromResult<(long Tokens, long Window)?>(null);
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
        if (_currentThread is not null)
        {
            _ = RestoreThreadContextUsageAsync(_currentThread);
            _ = RestoreThreadHistoryAsync(_currentThread);
        }
    }

    private void RemoveProjectFromList(ProjectEntry project)
    {
        if (_projects.Count <= 1)
        {
            MessageBox.Show(this, "В списке должен остаться хотя бы один проект.", "Проекты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(this,
            $"Убрать проект «{project.Name}» из списка? Папка, файлы и сохранённые чаты останутся на диске. Чтобы вернуть проект, добавьте ту же папку снова.",
            "Убрать проект из списка",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        _projects.Remove(project);
        _removedProjects.RemoveAll(item => item.Id == project.Id);
        _removedProjects.Add(project);
        if (_selectedProject?.Id == project.Id)
        {
            SelectProject(_projects[0]);
        }
        else
        {
            RenderProjects();
            SaveState();
        }
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
        if (_contextOpen)
        {
            RefreshFileTree();
            UpdateChatInfoPanel();
            _ = RefreshRepositoryInfoAsync();
            _repositoryRefreshTimer.Start();
        }
        else
        {
            _repositoryRefreshTimer.Stop();
        }
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
            _modelCatalogTask ??= CodexCliService.LoadModelCatalogAsync(DefaultEffort);
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

        var removed = _removedProjects.FirstOrDefault(project => string.Equals(project.Path, path, StringComparison.OrdinalIgnoreCase));
        if (removed is not null)
        {
            _removedProjects.Remove(removed);
            _projects.Add(removed);
            SelectProject(removed);
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
                    _removedProjects.AddRange(state.RemovedProjects.Where(project =>
                        !string.IsNullOrWhiteSpace(project.Id) &&
                        !string.IsNullOrWhiteSpace(project.Path) &&
                        !_projects.Any(active => active.Id == project.Id)));
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
                            ContextWindowTokens = stored.ContextWindowTokens,
                            ActiveRun = stored.ActiveRun
                        });
                    }
                    _promptQueue.AddRange(state.QueuedPrompts.Where(prompt =>
                        !string.IsNullOrWhiteSpace(prompt.Id) &&
                        !string.IsNullOrWhiteSpace(prompt.ThreadId) &&
                        !string.IsNullOrWhiteSpace(prompt.Text) &&
                        _threads.Any(thread => thread.Id == prompt.ThreadId)));
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
                RemovedProjects = _removedProjects,
                QueuedPrompts = _promptQueue,
                Threads = _threads.Select(thread => new StoredThread
                {
                    Id = thread.Id,
                    Title = thread.Title,
                    ProjectId = thread.ProjectId,
                    ProjectPath = thread.ProjectPath,
                    CodexSessionId = thread.CodexSessionId,
                    IsArchived = thread.IsArchived,
                    ContextTokens = thread.ContextTokens,
                    ContextWindowTokens = thread.ContextWindowTokens,
                    ActiveRun = thread.ActiveRun
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
        if (Control<Ellipse>("StatusDot") is { } dot) dot.Fill = Brush(isError ? "#E78C8C" : HasActiveRuns ? "#D7B66E" : "#77C99B");
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

    private void CleanupWindowResources()
    {
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
        public ActiveCliRun? ActiveRun { get; set; }
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
        public ActiveCliRun? ActiveRun { get; set; }
    }

    private sealed class QueuedPrompt
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ThreadId { get; set; } = "";
        public string Text { get; set; } = "";
        public string Model { get; set; } = DefaultModel;
        public string Effort { get; set; } = DefaultEffort;
        public bool FullAccess { get; set; }
    }

    private sealed class ActiveCliRun
    {
        public string RunId { get; set; } = Guid.NewGuid().ToString("N");
        public string Prompt { get; set; } = "";
        public string Model { get; set; } = DefaultModel;
        public string Effort { get; set; } = DefaultEffort;
        public bool FullAccess { get; set; }
        public string RequestPath { get; set; } = "";
        public string ScriptPath { get; set; } = "";
        public string StartedPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string ErrorPath { get; set; } = "";
        public string CompletionPath { get; set; } = "";
        public int WorkerProcessId { get; set; }
        public long WorkerStartTimeUtcTicks { get; set; }
    }

    private sealed class CodexCliWorkerRequest
    {
        public string RunId { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public List<string> Arguments { get; set; } = [];
        public string StartedPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string ErrorPath { get; set; } = "";
        public string CompletionPath { get; set; } = "";
    }

    private sealed class CodexCliWorkerCompletion
    {
        public int ExitCode { get; set; }
        public string? Error { get; set; }
        public string? SessionId { get; set; }
    }

    private sealed class CliOutputTail
    {
        public long Offset { get; set; }
        public List<byte> PendingBytes { get; } = [];
    }

    private sealed class StoredState
    {
        public string? SelectedProjectId { get; set; }
        public string? SelectedModel { get; set; }
        public string? SelectedEffort { get; set; }
        public List<ProjectEntry> Projects { get; set; } = [];
        public List<ProjectEntry> RemovedProjects { get; set; } = [];
        public List<QueuedPrompt> QueuedPrompts { get; set; } = [];
        public List<StoredThread> Threads { get; set; } = [];
    }

    private sealed record UsageSnapshot(IReadOnlyList<UsageWindow> Windows);
    private sealed record UsageWindow(long? DurationMinutes, int UsedPercent, long? ResetsAt);

    private sealed class ChatMessage(bool isUser, string text)
    {
        public bool IsUser { get; } = isUser;
        public string Text { get; set; } = text;
        public string? ActivityTitle { get; set; }
        public bool IsError { get; set; }
        public bool IsExpanded { get; set; }
        public bool HasCompletedMessages { get; set; }
    }
}
