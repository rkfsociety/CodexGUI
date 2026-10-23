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
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
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
    private const string PromptPlaceholder = "Опишите задачу или изменение проекта…";
    private const string DefaultModel = "gpt-6-luna";
    private const string DefaultEffort = "high";
    private static readonly JsonSerializerOptions StateSerializerOptions = new() { WriteIndented = true };
    private readonly ObservableCollection<ChatThread> _threads = [];
    private readonly List<ProjectEntry> _projects = [];
    private readonly List<ProjectEntry> _removedProjects = [];
    private readonly List<QueuedPrompt> _promptQueue = [];
    private readonly List<CodexActionDefinition> _actions = [];
    private readonly List<string> _composerAttachments = [];
    private readonly Dictionary<string, CodexThreadGoal?> _threadGoalCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _threadGoalLoadsInProgress = new(StringComparer.Ordinal);
    private readonly HashSet<string> _threadGoalLoadFailures = new(StringComparer.Ordinal);
    private IReadOnlyList<ModelOption> _availableModels = [];
    private Task<IReadOnlyList<ModelOption>>? _modelCatalogTask;
    private readonly Dictionary<string, FrameworkElement> _named = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _reloadDebounce;
    private readonly DispatcherTimer _stateSaveDebounce;
    private readonly DispatcherTimer _usageRefreshTimer;
    private readonly DispatcherTimer _updateCheckTimer;
    private readonly DispatcherTimer _repositoryRefreshTimer;
    private readonly DispatcherTimer _automationTimer;
    private readonly InputLanguageManager _inputLanguageManager;
    private readonly CancellationTokenSource _appServerCancellation = new();
    private CancellationTokenSource _threadSelectionCancellation = new();
    private readonly CancellationTokenSource _updateCheckCancellation = new();
    private readonly CancellationTokenSource _repositoryRefreshCancellation = new();
    private bool _isRefreshingUsage;
    private bool _isCheckingUpdates;
    private bool _isInstallingUpdate;
    private bool _isClosing;
    private bool _stateSaveBlocked;
    private PreparedAppUpdate? _preparedUpdate;
    private string? _availableUpdateVersion;
    private int? _updateDownloadPercent;
    private long _updateDownloadedBytes;
    private long? _updateTotalBytes;
    private string _updateDownloadStage = "";
    private string? _updateDownloadError;
    private GitRepositorySnapshot? _repositorySnapshot;
    private string? _snapshotProjectPath;
    private bool _isRefreshingRepository;
    private bool _repositoryRefreshPending;
    private bool _isReviewingChanges;
    private bool _isGitCommandRunning;
    private string _usageLimitsText = "Загружаю лимиты…";
    private string _usageLimitsTooltip = "Лимиты аккаунта";
    private FileSystemWatcher? _viewWatcher;
    private Forms.NotifyIcon? _trayIcon;
    private DrawingIcon? _trayImage;
    private string? _pendingTrayNotificationThreadId;
    private readonly Dictionary<string, Process?> _activeProcesses = new(StringComparer.Ordinal);
    private readonly List<CodexAutomationDefinition> _automationDefinitions = [];
    private readonly List<CodexAutomationRun> _automationRuns = [];
    private CodexAutomationsWindow? _automationWindow;
    private CodexActivityWindow? _activityWindow;
    private CodexTerminalWindow? _terminalWindow;
    private bool _isProcessingAutomationTick;
    private bool _isPumpingPromptQueue;
    private bool _isEditingQueuedPrompt;
    private bool _isSavingPrompt;
    private bool _sidebarCollapsed;
    private bool _sidebarAutoHideOverridden;
    private bool _contextOpen;
    private bool _showArchivedChats;
    private string _projectPath = FindProjectRoot();
    private ProjectEntry? _selectedProject;
    private string? _startupIssue;
    private string _selectedModel = DefaultModel;
    private string _selectedEffort = DefaultEffort;
    private ChatThread? _currentThreadValue;
    private ChatThread? _currentThread
    {
        get => _currentThreadValue;
        set
        {
            if (!ReferenceEquals(_currentThreadValue, value)) CancelSelectedThreadReads();
            _currentThreadValue = value;
        }
    }
    private string? _renderedConversationThreadId;
    private bool HasActiveRuns => _threads.Any(thread => thread.ActiveRun is not null);
    private bool IsCurrentThreadRunning => _currentThread?.ActiveRun is not null;
    private Grid? _activeView;
    private TextBlock? _streamingAssistantTextBlock;

    public MainWindow()
    {
        InitializeComponent();
        CodexWindowTheme.Apply(this);
        _inputLanguageManager = InputLanguageManager.Current;
        _inputLanguageManager.InputLanguageChanged += PromptInputLanguageChanged;
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
            RequestStateSave();
        };
        _usageRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _usageRefreshTimer.Tick += async (_, _) => await RefreshUsageLimitsAsync();
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateCheckTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
        _repositoryRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _repositoryRefreshTimer.Tick += async (_, _) =>
        {
            if (IsVisible) await RefreshRepositoryInfoAsync();
        };
        _automationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _automationTimer.Tick += async (_, _) => await ProcessAutomationsAsync();
        LoadState();
        try
        {
            _automationDefinitions.AddRange(CodexAutomationStore.LoadDefinitions());
            _automationRuns.AddRange(CodexAutomationStore.LoadRuns());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _startupIssue = "Не удалось загрузить расписания Codex: " + error.Message;
        }
        UpdateAutomationTimerInterval();
        CleanupObsoleteWorkerRuntimeCaches();
        ReloadView();
        InitializeTrayIcon();
        StartViewWatcher();
        _usageRefreshTimer.Start();
        Loaded += (_, _) =>
        {
            _ = RefreshUsageLimitsAsync();
            _updateCheckTimer.Start();
            _ = CheckForUpdatesAsync();
            _automationTimer.Start();
            _repositoryRefreshTimer.Start();
            _ = RefreshRepositoryInfoAsync();
            RenderPromptQueue();
            _ = ResumePendingWorkAsync();
            _ = ProcessAutomationsAsync();
        };
        Closed += (_, _) =>
        {
            _terminalWindow?.Close();
            _terminalWindow = null;
            _usageRefreshTimer.Stop();
            _updateCheckTimer.Stop();
            _automationTimer.Stop();
            _repositoryRefreshTimer.Stop();
            _reloadDebounce.Stop();
            _isClosing = true;
            _threadSelectionCancellation.Cancel();
            _threadSelectionCancellation.Dispose();
            _appServerCancellation.Cancel();
            _appServerCancellation.Dispose();
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
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) Hide();
        };
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_isCheckingUpdates || _isInstallingUpdate || _isClosing) return;
        if (_preparedUpdate is not null) return;
        if (AppInstaller.IsDevelopmentCheckout(Environment.ProcessPath ?? string.Empty)) return;

        _isCheckingUpdates = true;
        SetTrayTooltip("Codex — проверяю обновления");
        try
        {
            var update = await AppUpdater.FindUpdateAsync(_updateCheckCancellation.Token);
            if (_isClosing) return;
            if (update is null)
                return;

            _availableUpdateVersion = update.Version;
            _updateDownloadPercent = 0;
            _updateDownloadedBytes = 0;
            _updateTotalBytes = null;
            _updateDownloadStage = "Подготовка загрузки";
            _updateDownloadError = null;
            UpdateUpdateButton();
            SetTrayTooltip($"Codex — скачиваю обновление v{update.Version}");
            var progress = new Progress<UpdateDownloadProgress>(status =>
            {
                // Progress callbacks can be queued after the download has finished.
                if (_isClosing || _preparedUpdate is not null || _availableUpdateVersion != update.Version)
                    return;
                _updateDownloadStage = status.Stage;
                _updateDownloadPercent = status.Percent;
                _updateDownloadedBytes = status.DownloadedBytes;
                _updateTotalBytes = status.TotalBytes;
                UpdateUpdateButton();
            });
            var preparedUpdate = await AppUpdater.DownloadAndVerifyAsync(update, _updateCheckCancellation.Token, progress);
            if (_isClosing)
            {
                AppUpdater.DiscardPreparedUpdate(preparedUpdate);
                return;
            }

            _preparedUpdate = preparedUpdate;
            _updateDownloadPercent = 100;
            _updateDownloadStage = "Скачано и проверено";
            UpdateUpdateButton();
            SetTrayTooltip($"Codex — обновление v{update.Version} готово к установке");
        }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception error)
        {
            if (_isClosing) return;
            _updateDownloadError = _availableUpdateVersion is null ? null : error.Message;
            if (_updateDownloadError is not null)
            {
                _updateDownloadPercent = null;
                _updateDownloadStage = "Ошибка загрузки";
                UpdateUpdateButton();
            }
            SetTrayTooltip("Codex — не удалось загрузить обновление");
            Debug.WriteLine($"Не удалось проверить или скачать обновление CodexGUI: {error}");
        }
        finally
        {
            _isCheckingUpdates = false;
            if (!_isClosing && _preparedUpdate is null && _updateDownloadError is null)
                SetTrayTooltip("Codex — готов к работе");
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
            CodexMessageDialog.Show(this, $"Не удалось запустить установку обновления.\n\n{error.Message}", "Обновление CodexGUI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateUpdateButton()
    {
        if (Control<Border>("UpdateStatusPanel") is not { } panel ||
            Control<Button>("UpdateButton") is not { } button) return;

        panel.Visibility = _availableUpdateVersion is null ? Visibility.Collapsed : Visibility.Visible;
        if (_availableUpdateVersion is null) return;

        var ready = _preparedUpdate is not null && !_isInstallingUpdate;
        button.IsEnabled = ready;
        button.Cursor = ready ? Cursors.Hand : Cursors.Arrow;
        button.ToolTip = ready
            ? $"Обновление v{_availableUpdateVersion} скачано и проверено. Нажмите, чтобы установить и перезапустить Codex."
            : _updateDownloadError is not null
                ? $"Ошибка загрузки v{_availableUpdateVersion}: {_updateDownloadError}"
                : $"Обновление v{_availableUpdateVersion}: {_updateDownloadStage}. Установка доступна только после проверки.";

        AutomationProperties.SetName(button, button.ToolTip.ToString());

        if (Control<TextBlock>("UpdateProgressLabel") is { } label)
        {
            label.Text = _isInstallingUpdate ? "…" : _updateDownloadError is not null ? "!" :
                ready ? "✓" : _updateDownloadPercent is { } value ? $"{value}%" : "…";
            label.Foreground = Brush(_updateDownloadError is not null ? "#F0A5A5" : "#D1F9E8");
        }

        if (Control<System.Windows.Shapes.Path>("UpdateProgressArc") is { } arc)
        {
            arc.Stroke = Brush(_updateDownloadError is not null ? "#F0A5A5" : "#92E5C7");
            // The final full ring and install action are reserved for a verified download.
            var percent = ready || _isInstallingUpdate ? 100 : _updateDownloadError is not null ? 0
                : Math.Clamp(_updateDownloadPercent ?? 0, 0, 99);
            const double center = 23;
            const double radius = 19.5;
            if (percent == 0)
            {
                arc.Data = Geometry.Empty;
            }
            else if (percent == 100)
            {
                arc.Data = new EllipseGeometry(new Point(center, center), radius, radius);
            }
            else
            {
                var fraction = percent / 100.0;
                var angle = fraction * Math.PI * 2;
                var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
                var figure = new PathFigure
                {
                    StartPoint = new Point(center, center - radius), IsClosed = false, IsFilled = false
                };
                figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0,
                    fraction > 0.5, SweepDirection.Clockwise, true));
                arc.Data = new PathGeometry([figure]);
            }
        }
    }

    private void SetTrayTooltip(string text)
    {
        if (_trayIcon is not null) _trayIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private async Task HandleUpdateButtonClickAsync()
    {
        await InstallPreparedUpdateAsync();
    }

    private void InitializeTrayIcon()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить файл приложения.");
        _trayImage = DrawingIcon.ExtractAssociatedIcon(executable)
            ?? throw new InvalidOperationException("В приложении не найдена иконка для трея.");

        var menu = new Forms.ContextMenuStrip
        {
            BackColor = DrawingColor.FromArgb(22, 33, 43),
            ForeColor = DrawingColor.FromArgb(236, 242, 245),
            ShowImageMargin = false,
            RenderMode = Forms.ToolStripRenderMode.Professional,
            Renderer = new Forms.ToolStripProfessionalRenderer(new CodexTrayColorTable())
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
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ShowFromTray();
            if (_pendingTrayNotificationThreadId is not { } threadId) return;
            _pendingTrayNotificationThreadId = null;
            if (_threads.FirstOrDefault(thread => thread.Id == threadId) is not { } thread) return;
            _showArchivedChats = thread.IsArchived;
            SelectThread(thread);
        });
    }

    private void ShowChatCompletionNotification(ChatThread thread, bool failed)
    {
        if (_trayIcon is null || _isClosing) return;
        _pendingTrayNotificationThreadId = thread.Id;
        var title = failed ? "Запрос Codex завершился с ошибкой" : "Ответ Codex готов";
        var text = thread.Title.Length <= 240 ? thread.Title : thread.Title[..237] + "…";
        _trayIcon.ShowBalloonTip(5000, title, text,
            failed ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    internal void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _ = RefreshRepositoryInfoAsync();
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Views", "WorkspaceView.xaml"))
                || File.Exists(Path.Combine(directory.FullName, "Build", "CodexGUI.csproj")))
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
            root.SizeChanged += (_, _) => UpdatePanelLayout();
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
        Click("ExpandSidebarButton", (_, _) => ToggleSidebar());
        Click("RailNewChatButton", (_, _) => NewChat());
        Click("RailSearchButton", (_, _) => ShowSearch());
        Click("RailActivityButton", (_, _) => ShowActivityWindow());
        Click("RailAutomationsButton", async (_, _) => await ShowAutomationsAsync());
        Click("RailAgentsButton", async (_, _) => await ShowSubagentsAsync());
        Click("RailExtensionsButton", (_, _) => ShowExtensions(showSkills: false));
        Click("RailMcpServersButton", (_, _) => ShowMcpServers());
        Click("RailContextButton", (_, _) => ToggleContext());
        Click("RailActionsButton", (_, _) => ShowActionsWindow());
        Click("ChatAttachmentsButton", (_, _) => ShowChatAttachments());
        Click("ChatDiffButton", (_, _) => ToggleContext(true));
        Click("CloseContextButton", (_, _) => ToggleContext(false));
        Click("TerminalButton", (_, _) => ShowTerminalWindow());
        Click("RefreshRepositoryButton", (_, _) =>
        {
            RefreshFileTree();
            UpdateChatInfoPanel();
            _ = RefreshRepositoryInfoAsync();
        });
        Click("RepositoryRemoteButton", (_, _) => OpenRepositoryRemote());
        Click("ReviewChangesButton", async (_, _) => await ReviewUncommittedChangesAsync());
        Click("StageAllChangesButton", async (_, _) => await RunRepositoryGitCommandAsync(
            "Добавляю изменения в индекс…", "Изменения добавлены в индекс Git.",
            (root, token) => GitRepositoryInspector.StageAllAsync(root, token)));
        Click("UnstageAllChangesButton", async (_, _) => await RunRepositoryGitCommandAsync(
            "Снимаю изменения с индекса…", "Изменения убраны из индекса; файлы сохранены.",
            (root, token) => GitRepositoryInspector.UnstageAllAsync(root, token)));
        Click("CommitStagedButton", async (_, _) => await CommitStagedChangesAsync());
        Click("PushBranchButton", async (_, _) => await PushCurrentBranchAsync());
        Click("CreatePullRequestButton", async (_, _) => await CreatePullRequestAsync());
        if (Control<TextBox>("CommitMessageInput") is { } commitMessageInput)
            commitMessageInput.TextChanged += (_, _) => UpdateGitActionButtons();
        Click("OpenFolderButton", (_, _) => OpenProjectFolder());
        Click("ProjectButton", (_, _) => ChooseProjectFolder());
        Click("AddProjectButton", (_, _) => ShowProjectMenu());
        Click("ArchivedChatsButton", (_, _) => ToggleArchivedChats());
        Click("SendButton", async (_, _) => await SendPromptAsync());
        Click("FollowUpModeButton", (_, _) => ToggleFollowUpMode());
        Click("StopButton", async (_, _) => await InterruptCurrentTurnAsync());
        Click("ClearPromptQueueButton", (_, _) => ClearPromptQueue());
        Click("ModeButton", (_, _) => ShowAccessMenu());
        Click("ModelButton", async (_, _) => await ShowModelMenuAsync());
        Click("EffortButton", async (_, _) => await ShowEffortMenuAsync());
        Click("WorktreeButton", async (_, _) => await ToggleWorktreeModeAsync());
        Click("AddContextButton", (_, _) => AddFileContext());
        Click("ScreenshotButton", async (_, _) => await CaptureScreenshotAsync());
        Click("SkillsButton", (_, _) => ShowExtensions(showSkills: true));
        Click("ThreadGoalButton", async (_, _) => await ShowThreadGoalAsync());
        Click("ActionsButton", (_, _) => ShowActionsWindow());
        Click("UpdateButton", async (_, _) => await HandleUpdateButtonClickAsync());

        if (Control<TextBox>("PromptInput") is { } prompt)
        {
            prompt.Language = XmlLanguage.GetLanguage(_inputLanguageManager.CurrentInputLanguage.IetfLanguageTag);
            UpdatePromptSpellCheck(prompt);
            prompt.TextChanged += (_, _) => UpdatePromptSpellCheck(prompt);
            prompt.Foreground = Brush(prompt.Text == PromptPlaceholder ? "#8D95A2" : "#F1F3F5");
            prompt.GotKeyboardFocus += (_, _) =>
            {
                prompt.Language = XmlLanguage.GetLanguage(_inputLanguageManager.CurrentInputLanguage.IetfLanguageTag);
                if (prompt.Text == PromptPlaceholder)
                {
                    prompt.Clear();
                    prompt.Foreground = Brush("#F1F3F5");
                }
            };
            prompt.LostKeyboardFocus += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(prompt.Text))
                {
                    prompt.Text = PromptPlaceholder;
                    prompt.Foreground = Brush("#8D95A2");
                }
            };
            prompt.PreviewKeyDown += PromptInput_PreviewKeyDown;
        }
    }

    private void Click(string name, RoutedEventHandler handler)
    {
        if (Control<Button>(name) is { } button) button.Click += handler;
    }

    private void PromptInputLanguageChanged(object? sender, InputLanguageEventArgs e)
    {
        if (Control<TextBox>("PromptInput") is { IsKeyboardFocusWithin: true } prompt)
            prompt.Language = XmlLanguage.GetLanguage(e.NewLanguage.IetfLanguageTag);
    }

    private static void UpdatePromptSpellCheck(TextBox prompt)
    {
        var enabled = prompt.Text != PromptPlaceholder;
        if (SpellCheck.GetIsEnabled(prompt) != enabled)
            SpellCheck.SetIsEnabled(prompt, enabled);
    }

    private void RefreshView()
    {
        RenderComposerAttachments();
        if (!string.Equals(_snapshotProjectPath, _projectPath, StringComparison.OrdinalIgnoreCase))
            _repositorySnapshot = null;
        if (Control<TextBlock>("ProjectPathShort") is { } shortPath)
        {
            var projectName = _selectedProject?.Name
                ?? (Path.GetFileName(_currentThread?.ProjectPath?.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : _projectPath);
            shortPath.Text = _currentThread?.WorktreePath is null ? projectName : projectName + " · worktree";
        }
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
        UpdateWorktreeButton();
        RenderPromptQueue();
        UpdatePanelLayout();
        if (Control<Border>("ContextPanel") is { } contextPanel)
            contextPanel.Visibility = _contextOpen ? Visibility.Visible : Visibility.Collapsed;

        RenderThreads();
        RenderProjects();
        RenderConversation();
        if (_contextOpen) RefreshFileTree();
        _ = RefreshRepositoryInfoAsync();
        SetStatus(HasActiveRuns ? "В работе" : "Готов",
            _startupIssue ?? "Запросы выполняются в выбранной папке.",
            isError: _startupIssue is not null);
    }

    private void RenderThreads()
    {
        UpdateActivityView();
        if (Control<StackPanel>("ChatList") is not { } list) return;
        list.Children.Clear();
        var projectThreads = _threads.Where(thread =>
            thread.ProjectId == _selectedProject?.Id && thread.IsArchived == _showArchivedChats).Reverse().ToArray();

        void AddSectionHeader(string title)
        {
            list.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brush("#7F8B9A"),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 8, 0, 3)
            });
        }

        void AddThreadButton(ChatThread thread)
        {
            var isActive = thread.ActiveRun is not null;
            var isWaiting = thread.WaitingForInput;
            var hasUnreadResponse = thread.HasUnreadResponse;
            var button = new Button
            {
                Content = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                    }
                },
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 10, 58, 10),
                Margin = new Thickness(0, 2, 0, 2),
                Background = thread == _currentThread ? Brush("#29453F") : Brushes.Transparent,
                BorderBrush = thread == _currentThread ? Brush("#3E6C5A") : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Foreground = Brush(thread == _currentThread ? "#F1FBF7" : "#A5B5C3"),
                FontSize = 12,
                ToolTip = isWaiting ? "Ожидает вашего ответа · " + thread.Title
                    : isActive ? "Выполняется… " + thread.Title
                    : hasUnreadResponse ? "Новый ответ · " + thread.Title
                    : thread.Title
            };
            var content = (Grid)button.Content;
            content.Children.Add(new Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(0, 0, 8, 0),
                Fill = Brush(isWaiting ? "#E6BC70" : isActive || hasUnreadResponse ? "#92E5C7" : "#00000000"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var title = new TextBlock
            {
                Text = thread.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = hasUnreadResponse ? FontWeights.SemiBold : FontWeights.Normal
            };
            Grid.SetColumn(title, 1);
            content.Children.Add(title);
            AutomationProperties.SetName(button, isWaiting
                ? $"{thread.Title}, ожидает вашего ответа"
                : isActive
                ? $"{thread.Title}, выполняется"
                : hasUnreadResponse ? $"{thread.Title}, новый ответ" : thread.Title);
            button.Click += (_, _) => SelectThread(thread);
            button.PreviewMouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowThreadContextMenu(button, thread);
            };
            var row = new Grid();
            row.Children.Add(button);
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            var pin = CreateThreadIconButton(thread.IsPinned ? "Открепить" : "Закрепить",
                "M12,2 L14,8 L20,9 L15,13 L16,20 L12,16 L8,20 L9,13 L4,9 L10,8 Z",
                thread.IsPinned ? "#E6BC70" : "#A5B5C3");
            pin.Click += (_, _) => ToggleThreadPinned(thread);
            var archive = CreateThreadIconButton(thread.IsArchived ? "Вернуть из архива" : "Архивировать",
                thread.IsArchived
                    ? "M3,7 H21 V21 H3 Z M2,3 H22 V7 H2 Z M12,17 V10 M9,13 L12,10 L15,13"
                    : "M3,7 H21 V21 H3 Z M2,3 H22 V7 H2 Z M9,11 H15",
                "#A5B5C3");
            archive.IsEnabled = !isActive;
            archive.Click += async (_, _) =>
            {
                archive.IsEnabled = false;
                try { await SetThreadArchivedAsync(thread, !thread.IsArchived); }
                finally { archive.IsEnabled = thread.ActiveRun is null; }
            };
            actions.Children.Add(pin);
            actions.Children.Add(archive);
            row.Children.Add(actions);
            list.Children.Add(row);
        }

        if (_showArchivedChats)
        {
            foreach (var thread in projectThreads) AddThreadButton(thread);
            return;
        }

        var pinned = projectThreads.Where(thread => thread.IsPinned).ToArray();
        var recent = projectThreads.Where(thread => !thread.IsPinned).ToArray();
        if (pinned.Length > 0)
        {
            AddSectionHeader("ЗАКРЕПЛЁННЫЕ");
            foreach (var thread in pinned) AddThreadButton(thread);
        }
        if (recent.Length > 0)
        {
            if (pinned.Length > 0) AddSectionHeader("НЕДАВНИЕ");
            foreach (var thread in recent) AddThreadButton(thread);
        }
    }

    private IReadOnlyList<CodexActivityItem> BuildActivityItems()
    {
        return _threads
            .Where(thread => thread.WaitingForInput || thread.ActiveRun is not null || thread.HasUnreadResponse)
            .Select(thread =>
            {
                var waiting = thread.WaitingForInput;
                var active = thread.ActiveRun is not null;
                var status = waiting ? "Ждёт ответа" : active ? "Выполняется" : "Новый ответ";
                var description = waiting
                    ? "Нужно ответить на запрос Codex"
                    : active ? "Codex выполняет задачу" : "Ответ завершённого чата не прочитан";
                var accent = waiting ? "#E6BC70" : active ? "#92E5C7" : "#92E5C7";
                var projectName = _projects.FirstOrDefault(project => project.Id == thread.ProjectId)?.Name
                    ?? Path.GetFileName(thread.ProjectPath.TrimEnd(Path.DirectorySeparatorChar));
                var item = new CodexActivityItem(thread.Id, thread.Title, projectName, status, description, accent)
                {
                    RecentEvents = BuildRecentActivityEvents(thread)
                };
                return (Item: item, Priority: waiting ? 0 : active ? 1 : 2);
            })
            .OrderBy(entry => entry.Priority)
            .ThenBy(entry => entry.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => entry.Item)
            .ToArray();
    }

    private static IReadOnlyList<CodexActivityEvent> BuildRecentActivityEvents(ChatThread thread)
    {
        const int eventLimit = 3;
        var events = new List<CodexActivityEvent>(eventLimit);
        for (var index = thread.Messages.Count - 1; index >= 0 && events.Count < eventLimit; index--)
        {
            var message = thread.Messages[index];
            if (message.ActivityTitle is not { Length: > 0 } title) continue;
            var detail = string.Join(" ", message.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (detail.Length > 140) detail = detail[..139] + "…";
            events.Add(new CodexActivityEvent(title, detail, message.IsError));
        }
        events.Reverse();
        return events;
    }

    private void UpdateActivityView()
    {
        var items = BuildActivityItems();
        if (Control<Button>("RailActivityButton") is { } button)
        {
            if (Control<Border>("ActivityNotificationBadge") is { } badge)
                badge.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (Control<TextBlock>("ActivityNotificationCount") is { } count)
                count.Text = items.Count > 9 ? "9+" : items.Count.ToString(CultureInfo.InvariantCulture);
            var activityName = items.Count == 0 ? "Активность" : $"Активность: {items.Count}";
            button.ToolTip = items.Count == 0
                ? "Активные чаты, запросы на подтверждение и непрочитанные ответы"
                : $"Активность: элементов {items.Count}";
            AutomationProperties.SetName(button, activityName);
        }
        _activityWindow?.UpdateItems(items);
    }

    private void ShowActivityWindow()
    {
        if (_activityWindow is { IsVisible: true } existing)
        {
            UpdateActivityView();
            existing.Activate();
            return;
        }

        var window = new CodexActivityWindow(OpenActivityThread) { Owner = this };
        _activityWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activityWindow, window)) _activityWindow = null;
        };
        UpdateActivityView();
        window.Show();
    }

    private void OpenActivityThread(string threadId)
    {
        if (_threads.FirstOrDefault(thread => thread.Id == threadId) is not { } thread) return;
        _showArchivedChats = thread.IsArchived;
        SelectThread(thread);
    }

    private void SetThreadWaitingForInput(ChatThread thread, bool waiting)
    {
        if (thread.WaitingForInput == waiting) return;
        thread.WaitingForInput = waiting;
        RenderThreads();
    }

    private void ToggleThreadPinned(ChatThread thread)
    {
        thread.IsPinned = !thread.IsPinned;
        if (!SaveState())
        {
            thread.IsPinned = !thread.IsPinned;
            RenderThreads();
            SetStatus("Не удалось сохранить закрепление", _startupIssue ?? "Состояние CodexGUI не сохранено.", isError: true);
            return;
        }
        RenderThreads();
        SetStatus(thread.IsPinned ? "Чат закреплён" : "Чат откреплён",
            "Порядок закрепления хранится в CodexGUI на этом компьютере.");
    }

    private Button CreateThreadIconButton(string title, string iconData, string color)
    {
        var button = new Button
        {
            Width = 24,
            Height = 26,
            Padding = new Thickness(5),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = title,
            Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(iconData),
                Stretch = Stretch.Uniform,
                Stroke = Brush(color),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round
            }
        };
        AutomationProperties.SetName(button, title);
        return button;
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
        var pin = CreateThreadActionButton(thread.IsPinned ? "Открепить" : "Закрепить",
            "M12,2 L14,8 L20,9 L15,13 L16,20 L12,16 L8,20 L9,13 L4,9 L10,8 Z",
            thread.IsPinned ? "#E6BC70" : "#D5D6D9");
        pin.Click += (_, _) =>
        {
            popup.IsOpen = false;
            ToggleThreadPinned(thread);
        };
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
        var forkButton = CreateThreadActionButton("Ответвить чат", "M4,3 V8 C4,12 12,12 12,16 M9,13 L12,16 L15,13", "#92E5C7");
        forkButton.IsEnabled = thread.ActiveRun is null && !thread.IsForking && !string.IsNullOrWhiteSpace(thread.CodexSessionId);
        forkButton.ToolTip = forkButton.IsEnabled ? "Создать отдельный чат с копией истории" : "Ответвление доступно после первого запроса";
        forkButton.Click += async (_, _) =>
        {
            popup.IsOpen = false;
            await ForkThreadAsync(thread);
        };
        var delete = CreateThreadActionButton("Удалить чат", "M4,6 H16 M6,6 V17 H14 V6 M7,6 V4 H13 V6 M8,9 V14 M12,9 V14", "#F08C8C");
        delete.IsEnabled = thread.ActiveRun is null;
        delete.Click += async (_, _) =>
        {
            popup.IsOpen = false;
            await DeleteThreadAsync(thread);
        };
        actions.Children.Add(pin);
        actions.Children.Add(forkButton);
        actions.Children.Add(archive);
        actions.Children.Add(new Border { Height = 1, Background = Brush("#34363B"), Margin = new Thickness(7, 4, 7, 4) });
        actions.Children.Add(delete);

        popup.Child = new Border
        {
            Width = 194,
            Background = Brush("#1D1F23"),
            BorderBrush = Brush("#34373E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(5),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 4, Opacity = 0.38 },
            Child = actions
        };
        popup.IsOpen = true;
    }

    private async Task ForkThreadAsync(ChatThread source)
    {
        if (source.ActiveRun is not null || source.IsForking || string.IsNullOrWhiteSpace(source.CodexSessionId)) return;
        source.IsForking = true;
        SetStatus("Создаю ответвление чата…", "Копирую сохранённую историю Codex.");
        try
        {
            var result = await new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token)
                .ForkThreadAsync(source.CodexSessionId);
            if (!result.TryGetProperty("thread", out var codexThread)
                || GetString(codexThread, "id") is not { Length: > 0 } sessionId)
                throw new InvalidDataException("Codex не вернул идентификатор ответвлённого чата.");

            var fork = new ChatThread
            {
                Title = TrimTitle("Копия: " + source.Title),
                ProjectId = source.ProjectId,
                ProjectPath = GetString(codexThread, "cwd") ?? source.ProjectPath,
                CodexSessionId = sessionId
            };
            _threads.Add(fork);
            _currentThread = fork;
            _selectedProject = _projects.FirstOrDefault(project => project.Id == fork.ProjectId);
            _projectPath = _selectedProject?.Path ?? source.ProjectPath;
            _showArchivedChats = false;
            RequestStateSave();
            RefreshView();
            await RestoreThreadHistoryAsync(fork);
            if (fork.HistoryError is { } historyError)
            {
                SetStatus("Чат ответвлён, история не загружена", historyError, isError: true);
                return;
            }
            SetStatus("Чат ответвлён", "Исходный чат сохранён без изменений.");
        }
        catch (Exception error)
        {
            SetStatus("Не удалось ответвить чат", error.Message, isError: true);
            CodexMessageDialog.Show(this, error.Message, "Не удалось ответвить чат", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            source.IsForking = false;
            RenderThreads();
        }
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
            {
                var client = new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token);
                if (archived) await client.ArchiveThreadAsync(sessionId);
                else await client.UnarchiveThreadAsync(sessionId);
            }
            thread.IsArchived = archived;
            if (archived && _currentThread == thread)
                _currentThread = _threads.LastOrDefault(item => item != thread && item.ProjectId == thread.ProjectId && !item.IsArchived);
            if (!archived)
            {
                _showArchivedChats = false;
                _currentThread = thread;
            }
            RequestStateSave();
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
        var result = CodexMessageDialog.Show(this,
            $"Удалить чат «{thread.Title}»? Codex удалит историю и порождённые ответвления. Это действие нельзя отменить.",
            "Удалить чат", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (thread.CodexSessionId is { Length: > 0 } sessionId)
                await new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token).DeleteThreadAsync(sessionId);
            _threads.Remove(thread);
            _promptQueue.RemoveAll(prompt => prompt.ThreadId == thread.Id);
            if (_currentThread == thread)
                _currentThread = _threads.LastOrDefault(item => item.ProjectId == thread.ProjectId && item.IsArchived == _showArchivedChats);
            RequestStateSave();
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

    private async Task<UsageSnapshot> ReadUsageLimitsAsync()
    {
        var result = await new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token).ReadRateLimitsAsync();
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

        thread.HistoryRestoreTask = RestoreThreadHistoryCoreAsync(thread, thread.CodexSessionId,
            _currentThread == thread ? _threadSelectionCancellation.Token : default);
        return thread.HistoryRestoreTask;
    }

    private async Task RestoreThreadHistoryCoreAsync(ChatThread thread, string sessionId, CancellationToken cancellationToken)
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
            var result = await ReadThreadHistoryAsync(sessionId, cancellationToken);
            thread.Messages.Clear();
            thread.Messages.AddRange(ParseThreadMessages(result));
            thread.HistoryLoaded = true;
        }
        catch (OperationCanceledException) { }
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
            if (!_isClosing && _currentThread == thread)
            {
                RenderConversation();
                if (thread.HistoryLoaded && thread.HistoryError is null)
                    SetStatus("Готов", "История чата восстановлена.");
                if (cancellationToken.IsCancellationRequested)
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!_isClosing && _currentThread == thread) _ = RestoreThreadHistoryAsync(thread);
                    }));
            }
        }
    }

    private async Task<JsonElement> ReadThreadHistoryAsync(string sessionId, CancellationToken cancellationToken)
    {
        return await new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token).ReadThreadAsync(sessionId, cancellationToken);
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
        var attachments = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            switch (GetString(part, "type"))
            {
                case "text" when GetString(part, "text") is { Length: > 0 } text:
                    parts.Add(text);
                    break;
                case "localImage" when GetString(part, "path") is { Length: > 0 } path:
                    attachments.Add(path);
                    break;
                case "image":
                    parts.Add("[Изображение]");
                    break;
            }
        }
        return FormatPromptWithAttachments(string.Join(Environment.NewLine, parts), attachments);
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
        UpdateChatInfoPanel();
        _ = RefreshRepositoryInfoAsync();
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
                Background = isSelected ? Brush("#263344") : Brushes.Transparent,
                Foreground = Brush(isSelected ? "#F1F3F5" : "#A4A5AA"),
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
            var setupScript = new MenuItem { Header = "Настроить подготовку worktree…" };
            setupScript.Click += async (_, _) => await ConfigureWorktreeSetupAsync(project);
            projectMenu.Items.Add(setupScript);
            if (WorktreeSetupConfiguration.Exists(project.Path))
            {
                var removeSetupScript = new MenuItem { Header = "Убрать сценарий подготовки worktree…" };
                removeSetupScript.Click += (_, _) => RemoveWorktreeSetup(project);
                projectMenu.Items.Add(removeSetupScript);
            }
            projectMenu.Items.Add(new Separator());
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
        var scroll = Control<ScrollViewer>("ConversationScroll");
        var threadChanged = !string.Equals(_renderedConversationThreadId, _currentThread?.Id, StringComparison.Ordinal);
        var followTail = threadChanged || scroll is null || IsNearConversationBottom(scroll);
        var previousOffset = scroll?.VerticalOffset ?? 0;
        _renderedConversationThreadId = _currentThread?.Id;
        _streamingAssistantTextBlock = null;
        stack.Children.Clear();
        UpdateChatQuickOverview();
        if (_currentThread is null || _currentThread.Messages.Count == 0)
        {
            var isNewChat = _currentThread?.CodexSessionId is null;
            var title = isNewChat ? "С чего начнём?" : _currentThread?.Title ?? "С чего начнём?";
            var detail = _currentThread?.HistoryLoading == true
                ? "Загружаю историю из Codex…"
                : _currentThread?.HistoryError is { } historyError
                    ? $"Не удалось восстановить историю: {historyError}"
                    : _currentThread?.CodexSessionId is not null
                ? "Этот чат сохранён в Codex. Отправьте сообщение, чтобы продолжить разговор."
                    : "Опишите задачу простыми словами. Codex изучит проект, объяснит код или поможет внести изменения.";
            var suggestions = isNewChat
                ? new[] { "Покажи структуру проекта", "Найди и исправь ошибку", "Предложи улучшения кода" }
                : Array.Empty<string>();
            stack.Children.Add(ChatWelcomeView.Create(title, detail, suggestions, SendWelcomeSuggestion));
        }
        else
        {
            for (var index = 0; index < _currentThread.Messages.Count;)
            {
                var message = _currentThread.Messages[index];
                if (message.ActivityTitle is null)
                {
                    stack.Children.Add(CreateMessage(message));
                    index++;
                    continue;
                }

                var activities = new List<ChatMessage>();
                while (index < _currentThread.Messages.Count && _currentThread.Messages[index].ActivityTitle is not null)
                    activities.Add(_currentThread.Messages[index++]);
                stack.Children.Add(CreateActivityGroup(activities));
            }
        }
        if (scroll is not null)
            Dispatcher.BeginInvoke(() =>
            {
                if (followTail && (threadChanged || IsNearConversationBottom(scroll)))
                    scroll.ScrollToEnd();
                else if (!followTail)
                    scroll.ScrollToVerticalOffset(Math.Min(previousOffset, scroll.ScrollableHeight));
            }, DispatcherPriority.Background);
    }

    private static bool IsNearConversationBottom(ScrollViewer scroll) =>
        scroll.ScrollableHeight - scroll.VerticalOffset <= 72;

    private async void SendWelcomeSuggestion(string text)
    {
        if (Control<TextBox>("PromptInput") is not { } input) return;
        input.Text = text;
        input.CaretIndex = input.Text.Length;
        await SendPromptAsync();
    }

    private FrameworkElement CreateMessage(ChatMessage message)
    {
        if (message.ActivityTitle is not null) return CreateActivityGroup([message]);
        var outer = new StackPanel
        {
            Tag = message,
            HorizontalAlignment = message.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = message.IsUser ? 760 : 900,
            Margin = new Thickness(0, 14, 0, 18)
        };
        if (!message.IsUser)
        {
            var header = new DockPanel { Margin = new Thickness(4, 0, 0, 7) };
            var copyButton = new Button
            {
                Content = "⧉",
                ToolTip = "Копировать ответ",
                Padding = new Thickness(6, 2, 6, 2),
                Foreground = Brush("#8799A7"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
                IsHitTestVisible = false
            };
            void UpdateCopyButtonVisibility()
            {
                var visible = outer.IsMouseOver || copyButton.IsKeyboardFocusWithin;
                copyButton.Opacity = visible ? 1 : 0;
                copyButton.IsHitTestVisible = visible;
            }
            outer.MouseEnter += (_, _) => UpdateCopyButtonVisibility();
            outer.MouseLeave += (_, _) => UpdateCopyButtonVisibility();
            copyButton.GotKeyboardFocus += (_, _) => UpdateCopyButtonVisibility();
            copyButton.LostKeyboardFocus += (_, _) => UpdateCopyButtonVisibility();
            copyButton.Click += async (_, _) =>
            {
                try
                {
                    Clipboard.SetText(message.Text);
                    copyButton.Content = "✓";
                    copyButton.ToolTip = "Ответ скопирован";
                    await Task.Delay(1100);
                    if (!copyButton.IsLoaded) return;
                    copyButton.Content = "⧉";
                    copyButton.ToolTip = "Копировать ответ";
                }
                catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
                {
                    SetStatus("Не удалось скопировать ответ", error.Message, isError: true);
                }
            };
            DockPanel.SetDock(copyButton, Dock.Right);
            header.Children.Add(copyButton);
            header.Children.Add(new TextBlock { Text = "✳", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush("#92E5C7"), VerticalAlignment = VerticalAlignment.Center });
            outer.Children.Add(header);
        }

        var text = new TextBlock
        {
            Text = message.Text,
            FontSize = 16,
            LineHeight = 27,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(message.IsUser ? "#EAF4F0" : "#F1F3F5")
        };
        var isStreaming = !message.IsUser && IsCurrentThreadRunning
            && ReferenceEquals(_currentThread?.Messages.LastOrDefault(), message);
        if (isStreaming)
            _streamingAssistantTextBlock = text;
        if (message.IsUser)
        {
            var (messageText, attachments) = SplitPromptWithAttachments(message.Text);
            text.Text = messageText;
            var content = new StackPanel();
            if (!string.IsNullOrWhiteSpace(messageText)) content.Children.Add(text);
            if (attachments.Length > 0)
            {
                var previews = new WrapPanel { MaxWidth = 680, Margin = new Thickness(0, content.Children.Count > 0 ? 10 : 0, 0, 0) };
                foreach (var path in attachments)
                    previews.Children.Add(new Border
                    {
                        Child = CreateAttachmentPreview(path, 200, 140),
                        Background = Brush("#263A49"), CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(4), Margin = new Thickness(3), ToolTip = path
                    });
                content.Children.Add(previews);
            }
            var bubble = new Border { Background = Brush("#1A332E"), BorderBrush = Brush("#35594F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18, 18, 6, 18), Padding = new Thickness(19, 15, 19, 15), Child = content };
            outer.Children.Add(bubble);
        }
        else
        {
            var response = new Grid();
            response.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            response.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            response.Children.Add(new Border
            {
                Background = Brush("#42695E"),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 3, 14, 3)
            });
            var body = new Border
            {
                Background = Brush("#121B23"), BorderBrush = Brush("#263541"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5, 16, 16, 16),
                Padding = new Thickness(19, 16, 20, 17),
                Child = isStreaming ? text : ChatMarkdownRenderer.Render(message.Text)
            };
            Grid.SetColumn(body, 1);
            response.Children.Add(body);
            outer.Children.Add(response);
        }
        return outer;
    }

    private FrameworkElement CreateActivityGroup(IReadOnlyList<ChatMessage> activities)
    {
        var errorCount = activities.Count(message => message.IsError);
        var header = $"Работа Codex · {activities.Count}" + (errorCount > 0 ? $" · ошибок: {errorCount}" : "");
        var entries = new StackPanel();
        foreach (var message in activities)
        {
            var entry = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            entry.Children.Add(new TextBlock
            {
                Text = message.ActivityTitle,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(message.IsError ? "#F09999" : "#9FAFC7"),
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                entry.Children.Add(new TextBox
                {
                    Text = message.Text,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    Background = Brushes.Transparent,
                    Foreground = Brush("#AFB5C0"),
                    BorderThickness = new Thickness(0),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Margin = new Thickness(0, 5, 0, 0),
                    MaxHeight = 180,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                });
            }

            var subagents = message.Subagents.Count > 0
                ? message.Subagents
                : message.RelatedThreadId is { Length: > 0 } legacyId
                    ? [new CodexSubagentInfo(legacyId, "spawn_agent", null, "running", null)]
                    : [];
            foreach (var subagent in subagents)
            {
                var openSubagent = new Button
                {
                    Content = subagents.Count > 1 ? $"Открыть · {SubagentStatusName(subagent.Status)}" : "Открыть субагента",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(9, 5, 9, 5),
                    Margin = new Thickness(0, 6, 0, 0),
                    Background = Brush("#263344"),
                    Foreground = Brush("#C8D4E2"),
                    FontSize = 10
                };
                openSubagent.ToolTip = $"{SubagentStatusName(subagent.Status)} · {subagent.ThreadId}";
                openSubagent.Click += async (_, _) => await OpenSubagentThreadAsync(subagent.ThreadId);
                entry.Children.Add(openSubagent);
            }
            entries.Children.Add(entry);
        }

        var expander = new Expander
        {
            Header = header,
            Content = new ScrollViewer
            {
                Content = entries,
                MaxHeight = 460,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            },
            IsExpanded = activities.Any(message => message.IsExpanded),
            Foreground = Brush(errorCount > 0 ? "#F09999" : "#9FAFC7"),
            FontSize = 12
        };
        expander.Expanded += (_, _) =>
        {
            foreach (var message in activities) message.IsExpanded = true;
        };
        expander.Collapsed += (_, _) =>
        {
            foreach (var message in activities) message.IsExpanded = false;
        };
        return new Border
        {
            Background = Brush("#182733"),
            BorderBrush = Brush(errorCount > 0 ? "#8E5357" : "#304657"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(13, 9, 13, 9),
            MaxWidth = 900,
            Margin = new Thickness(5, 5, 5, 9),
            Child = expander
        };
    }

    private static void UpsertTimelineItem(List<ChatMessage> messages, ChatMessage anchor,
        Dictionary<string, ChatMessage> items, string? id, ChatMessage message)
    {
        if (id is not null && items.TryGetValue(id, out var existing))
        {
            existing.Text = message.Text;
            existing.ActivityTitle = message.ActivityTitle;
            existing.IsError = message.IsError;
            existing.RelatedThreadId = message.RelatedThreadId;
            existing.Subagents = message.Subagents;
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
            "collabToolCall" => "Субагент",
            "web_search" or "webSearch" => "Поиск в интернете",
            "todo_list" or "plan" => "План работы",
            "error" => "Ошибка",
            _ => null
        };
        if (title is null) return null;
        var failed = type == "error" || GetString(item, "status") is "failed" or "declined" ||
            (item.TryGetProperty("exit_code", out var exit) && exit.TryGetInt32(out var code) && code != 0);
        var isCollab = type == "collabToolCall";
        var parts = new List<string>();
        foreach (var key in isCollab
                 ? new[] { "prompt" }
                 : new[] { "command", "text", "query", "server", "tool", "aggregated_output", "aggregatedOutput" })
            if (GetString(item, key) is { Length: > 0 } value) parts.Add(value);
        if (isCollab)
        {
            var tool = GetString(item, "tool") ?? "";
            if (tool.Length > 0) parts.Insert(0, "Операция: " + tool);
            if (item.TryGetProperty("agentsStates", out var states) && states.ValueKind == JsonValueKind.Object)
                foreach (var state in states.EnumerateObject())
                {
                    var status = GetString(state.Value, "status");
                    var detail = GetString(state.Value, "message");
                    parts.Add($"Агент {state.Name}: {status ?? "состояние неизвестно"}" +
                        (string.IsNullOrWhiteSpace(detail) ? "" : " · " + detail));
                }
        }
        foreach (var key in new[] { "summary", "content", "changes", "items", "arguments", "result", "error" })
            if (item.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null)
                parts.Add(value.ToString());
        var subagents = isCollab ? ParseSubagentReferences(item) : [];
        var agentError = subagents.Any(agent => agent.Status is "errored" or "notFound");
        return new ChatMessage(false, string.Join(Environment.NewLine + Environment.NewLine, parts))
        {
            ActivityTitle = $"{(failed ? "✕" : completed ? "✓" : "●")}  {title} · {(failed ? "ошибка" : completed ? "завершено" : "выполняется")}",
            IsError = failed || agentError,
            RelatedThreadId = subagents.FirstOrDefault()?.ThreadId,
            Subagents = subagents
        };
    }

    private static List<CodexSubagentInfo> ParseSubagentReferences(JsonElement item)
    {
        var threadIds = new List<string>();
        if (item.TryGetProperty("receiverThreadIds", out var receivers) && receivers.ValueKind == JsonValueKind.Array)
            threadIds.AddRange(receivers.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!));
        if (threadIds.Count == 0)
        {
            var legacyId = GetString(item, "newThreadId") ?? GetString(item, "receiverThreadId");
            if (!string.IsNullOrWhiteSpace(legacyId)) threadIds.Add(legacyId);
        }

        var prompt = GetString(item, "prompt");
        var tool = GetString(item, "tool") ?? "";
        var itemStatus = GetString(item, "status") ?? "running";
        var states = item.TryGetProperty("agentsStates", out var statesValue) && statesValue.ValueKind == JsonValueKind.Object
            ? statesValue
            : default;
        return threadIds.Distinct(StringComparer.Ordinal).Select(threadId =>
        {
            var state = states.ValueKind == JsonValueKind.Object && states.TryGetProperty(threadId, out var found) ? found : default;
            return new CodexSubagentInfo(
                threadId,
                tool,
                prompt,
                state.ValueKind == JsonValueKind.Object ? GetString(state, "status") ?? itemStatus : itemStatus,
                state.ValueKind == JsonValueKind.Object ? GetString(state, "message") : null);
        }).ToList();
    }

    private static string SubagentStatusName(string status) => status switch
    {
        "pendingInit" => "запускается",
        "running" or "inProgress" => "работает",
        "interrupted" => "прерван",
        "completed" => "завершён",
        "errored" or "failed" => "ошибка",
        "shutdown" => "остановлен",
        "notFound" => "не найден",
        _ => status
    };

    private async Task OpenSubagentThreadAsync(string sessionId)
    {
        var existing = _threads.FirstOrDefault(thread => thread.CodexSessionId == sessionId);
        if (existing is not null)
        {
            SelectThread(existing);
            return;
        }

        var parent = _currentThread;
        var projectId = parent?.ProjectId ?? _selectedProject?.Id;
        if (projectId is null) return;
        var subagent = new ChatThread
        {
            Title = "Субагент · " + (parent?.Title ?? "задача"),
            ProjectId = projectId,
            ProjectPath = parent?.ProjectPath ?? _projectPath,
            WorktreePath = parent?.WorktreePath,
            CodexSessionId = sessionId
        };
        _threads.Add(subagent);
        _currentThread = subagent;
        _selectedProject = _projects.FirstOrDefault(project => project.Id == projectId);
        _projectPath = subagent.WorktreePath ?? _selectedProject?.Path ?? subagent.ProjectPath;
        RequestStateSave();
        RefreshView();
        await RestoreThreadHistoryAsync(subagent);
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

    private string[] GetChatAttachments()
    {
        var prompts = (_currentThread?.Messages.Where(message => message.IsUser).Select(message => message.Text)
                ?? Enumerable.Empty<string>())
            .Concat(_currentThread is null ? Enumerable.Empty<string>()
                : _promptQueue.Where(prompt => prompt.ThreadId == _currentThread.Id).Select(prompt => prompt.Text));
        return prompts.SelectMany(prompt => SplitPromptWithAttachments(prompt).Attachments)
            .Concat(_composerAttachments)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatPromptWithAttachments(string text, IReadOnlyCollection<string> attachments)
    {
        const string prefix = "Файл для контекста: ";
        var attachmentText = string.Join(Environment.NewLine, attachments.Select(path => prefix + path));
        if (string.IsNullOrWhiteSpace(text)) return attachmentText;
        return attachmentText.Length == 0 ? text : text + Environment.NewLine + attachmentText;
    }

    private static (string Text, string[] Attachments) SplitPromptWithAttachments(string prompt)
    {
        const string prefix = "Файл для контекста: ";
        const string legacyImagePrefix = "[Изображение: ";
        var lines = new List<string>();
        var attachments = new List<string>();
        foreach (var line in prompt.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.Trim();
            var path = trimmed.StartsWith(prefix, StringComparison.Ordinal) ? trimmed[prefix.Length..].Trim().Trim('"')
                : trimmed.StartsWith(legacyImagePrefix, StringComparison.Ordinal) && trimmed.EndsWith(']')
                    ? trimmed[legacyImagePrefix.Length..^1].Trim().Trim('"') : null;
            if (string.IsNullOrWhiteSpace(path)) lines.Add(line);
            else attachments.Add(path);
        }
        return (string.Join(Environment.NewLine, lines).TrimEnd(), attachments.ToArray());
    }

    private void AddComposerAttachments(IEnumerable<string> paths)
    {
        var changed = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var fullPath = Path.GetFullPath(path);
            if (_composerAttachments.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) continue;
            _composerAttachments.Add(fullPath);
            changed = true;
        }
        if (!changed) return;
        if (Control<TextBox>("PromptInput") is { Text: PromptPlaceholder } input)
        {
            input.Clear();
            input.Foreground = Brush("#F1F3F5");
        }
        RenderComposerAttachments();
    }

    private void RemoveComposerAttachment(string path)
    {
        if (_composerAttachments.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
        RenderComposerAttachments();
    }

    private void ClearComposerAttachments()
    {
        if (_composerAttachments.Count == 0) return;
        _composerAttachments.Clear();
        RenderComposerAttachments();
    }

    private void RenderComposerAttachments()
    {
        if (Control<Border>("ComposerAttachmentsBorder") is not { } border ||
            Control<WrapPanel>("ComposerAttachmentsPanel") is not { } panel) return;

        panel.Children.Clear();
        border.Visibility = _composerAttachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var path in _composerAttachments)
        {
            var label = Path.GetFileName(path);
            var chip = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        CreateAttachmentPreview(path, 96, 58),
                        new TextBlock { Text = "×", FontSize = 14, Margin = new Thickness(6, -2, 0, 0), Foreground = Brush("#9FB2C3") }
                    }
                },
                MinHeight = 28,
                MaxWidth = 240,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(2),
                Background = Brush("#263A49"),
                Foreground = Brush("#DCE7ED"),
                BorderBrush = Brush("#405869"),
                BorderThickness = new Thickness(1),
                ToolTip = path,
                Tag = path
            };
            AutomationProperties.SetName(chip, $"Вложение: {label}. Удалить");
            chip.Click += (_, _) => RemoveComposerAttachment((string)chip.Tag);
            panel.Children.Add(chip);
        }
        UpdateChatQuickOverview();
    }

    private static FrameworkElement CreateAttachmentPreview(string path, int width, int height)
    {
        string? previewError = null;
        if (IsImageAttachment(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = width * 2;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                var image = new System.Windows.Controls.Image
                {
                    Source = bitmap, Width = width, Height = height,
                    Stretch = Stretch.Uniform, ToolTip = path
                };
                AutomationProperties.SetName(image, Path.GetFileName(path));
                return image;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or NotSupportedException or FileFormatException or ArgumentException)
            {
                previewError = $"Не удалось показать изображение: {error.Message}";
            }
        }
        return new TextBlock
        {
            Text = "▤ " + Path.GetFileName(path), MaxWidth = width,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("#DCE7ED"), ToolTip = previewError ?? path
        };
    }

    private static bool IsImageAttachment(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".apng" or ".bmp" or ".gif" or ".jpeg" or ".jpg" or ".png" or ".tif" or ".tiff" or ".webp";

    private void UpdateChatQuickOverview()
    {
        var attachments = GetChatAttachments();
        if (Control<TextBlock>("ChatAttachmentsCount") is { } count)
            count.Text = attachments.Length.ToString(CultureInfo.CurrentCulture);
        if (Control<TextBlock>("ChatAttachmentsPreview") is { } preview)
        {
            preview.Text = attachments.Length switch
            {
                0 => "Нет прикреплённых файлов",
                1 => Path.GetFileName(attachments[0]),
                _ => Path.GetFileName(attachments[^1]) + $"  и ещё {attachments.Length - 1}"
            };
            preview.ToolTip = attachments.Length == 0 ? null : string.Join("\n", attachments);
        }
        var snapshot = string.Equals(_snapshotProjectPath, _projectPath, StringComparison.OrdinalIgnoreCase)
            ? _repositorySnapshot : null;
        var hasGitChanges = snapshot?.Changes.Count is > 0
            || snapshot?.UnpushedAddedLines is > 0
            || snapshot?.UnpushedRemovedLines is > 0;
        if (Control<Border>("ChatOverviewCard") is { } overview)
            overview.Visibility = attachments.Length > 0 || hasGitChanges ? Visibility.Visible : Visibility.Collapsed;
        if (Control<TextBlock>("GitAddedLines") is { } added)
            added.Text = snapshot?.UnpushedAddedLines is { } unpushedAdded ? $"+{unpushedAdded:N0}" : "+—";
        if (Control<TextBlock>("GitRemovedLines") is { } removed)
            removed.Text = snapshot?.UnpushedRemovedLines is { } unpushedRemoved ? $"−{unpushedRemoved:N0}" : "−—";
        if (Control<Button>("ChatDiffButton") is { } changes)
            changes.ToolTip = snapshot?.IsRepository == true
                ? snapshot.UnpushedAddedLines is { } addedLines && snapshot.UnpushedRemovedLines is { } removedLines
                    ? $"{snapshot.Branch} · +{addedLines:N0} / −{removedLines:N0} строк не отправлено; файлов в рабочем дереве: {snapshot.Changes.Count}. Открыть подробности."
                    : $"{snapshot.Branch} · не удалось определить upstream для сравнения. Открыть подробности."
                : "Данные об изменениях Git недоступны";
    }

    private void ShowChatAttachments()
    {
        if (Control<Button>("ChatAttachmentsButton") is not { } anchor) return;
        var attachments = GetChatAttachments();
        var popup = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            Background = Brush("#24272D"),
            Foreground = Brush("#EAF3F6"),
            BorderBrush = Brush("#435D6D"),
            BorderThickness = new Thickness(1),
            MinWidth = 260,
            MaxWidth = 480
        };
        if (attachments.Length == 0)
            popup.Items.Add(new MenuItem { Header = "В этом чате пока нет вложений", IsEnabled = false });
        foreach (var path in attachments)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(CreateAttachmentPreview(path, 72, 48));
            if (IsImageAttachment(path))
                header.Children.Add(new TextBlock
                {
                    Text = Path.GetFileName(path), MaxWidth = 310,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
                });
            var item = new MenuItem { Header = header, ToolTip = path, FontSize = 12 };
            item.Click += (_, _) =>
            {
                if (!File.Exists(path))
                {
                    SetStatus("Вложение недоступно", $"Не найден файл: {path}", isError: true);
                    return;
                }
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    SetStatus("Не удалось открыть вложение", error.Message, isError: true);
                }
            };
            popup.Items.Add(item);
        }
        anchor.ContextMenu = popup;
        popup.IsOpen = true;
    }

    private void UpdateChatInfoPanel()
    {
        UpdateThreadGoalButton();
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

    private void UpdateThreadGoalButton()
    {
        if (Control<Button>("ThreadGoalButton") is not { } button) return;
        var thread = _currentThread;
        if (thread is null)
        {
            button.IsEnabled = false;
            SetThreadGoalButtonText(button, "Цель");
            button.ToolTip = "Выберите чат, чтобы поставить цель";
            return;
        }

        if (thread.PendingGoal is { } pendingGoal)
        {
            var summary = CreateThreadGoalSummary(pendingGoal.Objective);
            button.IsEnabled = thread.ActiveRun is null;
            SetThreadGoalButtonText(button, thread.ActiveRun is not null
                ? "Цель · применяется…"
                : thread.CodexSessionId is null
                    ? $"Цель · черновик · {ThreadGoalStatusLabel(pendingGoal.Status)} · {summary}"
                    : $"Цель · синхронизировать · {ThreadGoalStatusLabel(pendingGoal.Status)} · {summary}");
            button.ToolTip = thread.ActiveRun is not null
                ? $"Цель будет сохранена в App Server перед первым ходом.\n{pendingGoal.Objective}"
                : $"Цель пока хранится в CodexGUI и будет синхронизирована перед следующим ходом.\n{pendingGoal.Objective}";
            return;
        }

        var threadId = thread.CodexSessionId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            button.IsEnabled = thread.ActiveRun is null;
            SetThreadGoalButtonText(button, thread.ActiveRun is null ? "Поставить цель" : "Цель · создаётся…");
            button.ToolTip = thread.ActiveRun is null
                ? "Сохранить цель в CodexGUI и отправить её в App Server перед первым запросом"
                : "Сессия чата создаётся; цель можно поставить после этого хода";
            return;
        }

        if (_threadGoalCache.TryGetValue(threadId, out var goal) && goal is not null)
        {
            button.IsEnabled = true;
            var summary = CreateThreadGoalSummary(goal.Objective);
            SetThreadGoalButtonText(button, string.IsNullOrEmpty(summary)
                ? $"Цель · {ThreadGoalStatusLabel(goal.Status)}"
                : $"Цель · {ThreadGoalStatusLabel(goal.Status)} · {summary}");
            button.ToolTip = $"{ThreadGoalStatusLabel(goal.Status)}\n{goal.Objective}";
            return;
        }

        if (_threadGoalCache.ContainsKey(threadId))
        {
            button.IsEnabled = true;
            SetThreadGoalButtonText(button, "Поставить цель");
            button.ToolTip = "Создать цель для этого чата";
            return;
        }

        if (_threadGoalLoadsInProgress.Contains(threadId))
        {
            button.IsEnabled = false;
            SetThreadGoalButtonText(button, "Цель · загрузка…");
            button.ToolTip = "Загружаю цель чата из Codex App Server";
            return;
        }

        button.IsEnabled = true;
        SetThreadGoalButtonText(button,
            _threadGoalLoadFailures.Contains(threadId) ? "Цель · повторить" : "Цель · загрузить…");
        button.ToolTip = _threadGoalLoadFailures.Contains(threadId)
            ? "Не удалось загрузить цель. Нажмите, чтобы повторить и открыть редактор."
            : "Загружаю сохранённую цель чата";
        if (!_threadGoalLoadFailures.Contains(threadId) && _threadGoalLoadsInProgress.Add(threadId))
            _ = LoadThreadGoalForDisplayAsync(threadId);
    }

    private void SetThreadGoalButtonText(Button button, string text)
    {
        if (Control<TextBlock>("ThreadGoalLabel") is { } label) label.Text = text;
        else button.Content = text;
        AutomationProperties.SetName(button, text);
    }

    private void CancelSelectedThreadReads()
    {
        if (_isClosing) return;
        _threadSelectionCancellation.Cancel();
        _threadSelectionCancellation.Dispose();
        _threadSelectionCancellation = new CancellationTokenSource();
    }

    private async Task LoadThreadGoalForDisplayAsync(string threadId)
    {
        try
        {
            var client = new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token);
            var goal = ReadThreadGoal(await client.ReadThreadGoalAsync(threadId, _threadSelectionCancellation.Token));
            if (_isClosing) return;
            if (!_threadGoalCache.ContainsKey(threadId)) _threadGoalCache[threadId] = goal;
            _threadGoalLoadFailures.Remove(threadId);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_isClosing) return;
            _threadGoalLoadFailures.Add(threadId);
            System.Diagnostics.Trace.TraceWarning("Не удалось загрузить цель чата {0}: {1}", threadId, error.Message);
        }
        finally
        {
            _threadGoalLoadsInProgress.Remove(threadId);
            if (!_isClosing && _currentThread?.CodexSessionId == threadId) UpdateThreadGoalButton();
        }
    }

    private static string CreateThreadGoalSummary(string objective)
    {
        var summary = string.Join(" ", objective.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        const int maximumLength = 24;
        return summary.Length <= maximumLength ? summary : summary[..(maximumLength - 1)].TrimEnd() + "…";
    }

    private async Task ShowThreadGoalAsync(string? initialObjective = null, string? initialStatus = null)
    {
        var thread = _currentThread;
        if (thread is null)
        {
            CodexMessageDialog.Show(this, "Сначала выберите или создайте чат.",
                "Цель чата", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (thread.ActiveRun is not null)
        {
            SetStatus("Цель сейчас недоступна", "Дождитесь завершения текущего хода Codex.");
            return;
        }

        try
        {
            var threadId = thread.CodexSessionId;
            var isPending = thread.PendingGoal is not null || string.IsNullOrWhiteSpace(threadId);
            CodexAppServerClient? client = null;
            CodexThreadGoal? existing;
            if (thread.PendingGoal is { } pendingGoal)
                existing = new CodexThreadGoal(pendingGoal.Objective, pendingGoal.Status, pendingGoal.TokenBudget, 0, 0);
            else if (!string.IsNullOrWhiteSpace(threadId))
            {
                client = new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token);
                var response = await client.ReadThreadGoalAsync(threadId);
                if (_isClosing) return;
                existing = ReadThreadGoal(response);
                _threadGoalCache[threadId] = existing;
                _threadGoalLoadFailures.Remove(threadId);
                UpdateThreadGoalButton();
            }
            else existing = null;

            var editor = new CodexThreadGoalWindow(existing, initialObjective, initialStatus, isPending) { Owner = this };
            if (editor.ShowDialog() != true) return;
            if (editor.ClearRequested)
            {
                if (string.IsNullOrWhiteSpace(threadId))
                {
                    var previous = thread.PendingGoal;
                    thread.PendingGoal = null;
                    if (!await SaveStateAsync())
                    {
                        thread.PendingGoal = previous;
                        SetStatus("Не удалось удалить черновик цели", _startupIssue ?? "Не удалось сохранить состояние чата.", isError: true);
                        return;
                    }
                    UpdateThreadGoalButton();
                    SetStatus("Черновик цели удалён", "Изменение сохранено в CodexGUI на этом компьютере.");
                    return;
                }

                client ??= new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token);
                var cleared = await client.ClearThreadGoalAsync(threadId);
                thread.PendingGoal = null;
                _threadGoalCache[threadId] = null;
                _threadGoalLoadFailures.Remove(threadId);
                RequestStateSave();
                UpdateThreadGoalButton();
                var didClear = cleared.TryGetProperty("cleared", out var clearedValue)
                    && clearedValue.ValueKind == JsonValueKind.True;
                SetStatus(didClear ? "Цель очищена" : "Цели уже нет", "Изменение сохранено в истории App Server.");
                return;
            }

            var objective = editor.Objective ?? throw new InvalidDataException("Укажите цель чата.");
            var status = editor.GoalStatus ?? "active";
            if (string.IsNullOrWhiteSpace(threadId))
            {
                var previous = thread.PendingGoal;
                thread.PendingGoal = new CodexAppServerGoalDraft
                {
                    Objective = objective,
                    Status = status,
                    TokenBudget = editor.TokenBudget
                };
                if (!await SaveStateAsync())
                {
                    thread.PendingGoal = previous;
                    SetStatus("Не удалось сохранить черновик цели", _startupIssue ?? "Не удалось сохранить состояние чата.", isError: true);
                    return;
                }
                UpdateThreadGoalButton();
                SetStatus("Черновик цели сохранён", "Цель будет отправлена в App Server перед первым ходом.");
                return;
            }

            client ??= new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token);
            var saved = await client.SetThreadGoalAsync(threadId, objective, status, editor.TokenBudget);
            var updatedGoal = ReadThreadGoal(saved)
                ?? throw new InvalidDataException("Codex App Server не вернул сохранённую цель.");
            thread.PendingGoal = null;
            _threadGoalCache[threadId] = updatedGoal;
            _threadGoalLoadFailures.Remove(threadId);
            RequestStateSave();
            UpdateThreadGoalButton();
            SetStatus("Цель чата сохранена", ThreadGoalStatusLabel(updatedGoal.Status));
        }
        catch (Exception error)
        {
            if (!_isClosing) SetStatus("Не удалось обновить цель чата", error.Message, isError: true);
        }
    }

    private async Task HandleThreadGoalCommandAsync(string command)
    {
        switch (command.Trim().ToLowerInvariant())
        {
            case "":
            case "edit":
            case "status":
                await ShowThreadGoalAsync();
                return;
            case "pause":
                await SetThreadGoalStatusAsync("paused");
                return;
            case "resume":
                await SetThreadGoalStatusAsync("active");
                return;
            case "complete":
                await SetThreadGoalStatusAsync("complete");
                return;
            case "clear":
                await ClearThreadGoalAsync();
                return;
            default:
                await ShowThreadGoalAsync(command.Trim(), "active");
                return;
        }
    }

    private async Task SetThreadGoalStatusAsync(string status)
    {
        var thread = _currentThread;
        if (thread is null)
        {
            SetStatus("Цель недоступна", "Сначала выберите чат.");
            return;
        }
        if (thread.ActiveRun is not null)
        {
            SetStatus("Цель сейчас недоступна", "Дождитесь завершения текущего хода Codex.");
            return;
        }
        if (thread.PendingGoal is { } pendingGoal)
        {
            var threadIdForPending = thread.CodexSessionId;
            if (string.IsNullOrWhiteSpace(threadIdForPending))
            {
                var previousStatus = pendingGoal.Status;
                pendingGoal.Status = status;
                if (!await SaveStateAsync())
                {
                    pendingGoal.Status = previousStatus;
                    SetStatus("Не удалось изменить статус цели", _startupIssue ?? "Не удалось сохранить состояние чата.", isError: true);
                    return;
                }
                UpdateThreadGoalButton();
                SetStatus("Статус черновика цели изменён", ThreadGoalStatusLabel(status));
                return;
            }
            try
            {
                var pendingClient = new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token);
                var response = await pendingClient.SetThreadGoalAsync(threadIdForPending,
                    pendingGoal.Objective, status, pendingGoal.TokenBudget);
                var updated = ReadThreadGoal(response)
                    ?? throw new InvalidDataException("Codex App Server не вернул обновлённую цель.");
                thread.PendingGoal = null;
                _threadGoalCache[threadIdForPending] = updated;
                _threadGoalLoadFailures.Remove(threadIdForPending);
                RequestStateSave();
                UpdateThreadGoalButton();
                SetStatus("Статус цели изменён", ThreadGoalStatusLabel(updated.Status));
                return;
            }
            catch (Exception error)
            {
                if (!_isClosing) SetStatus("Не удалось изменить статус цели", error.Message, isError: true);
                return;
            }
        }
        if (thread.CodexSessionId is not { Length: > 0 } threadId)
        {
            SetStatus("Цель недоступна", "Сначала отправьте первый запрос, чтобы создать сессию чата.");
            return;
        }
        try
        {
            var client = new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token);
            var current = ReadThreadGoal(await client.ReadThreadGoalAsync(threadId));
            if (current is null)
            {
                SetStatus("Цель ещё не задана", "Используйте /goal или кнопку «Цель», чтобы создать её.");
                return;
            }
            var response = await client.SetThreadGoalAsync(threadId, current.Objective, status, current.TokenBudget);
            var updated = ReadThreadGoal(response)
                ?? throw new InvalidDataException("Codex App Server не вернул обновлённую цель.");
            _threadGoalCache[threadId] = updated;
            _threadGoalLoadFailures.Remove(threadId);
            UpdateThreadGoalButton();
            SetStatus("Статус цели изменён", ThreadGoalStatusLabel(updated.Status));
        }
        catch (Exception error)
        {
            if (!_isClosing) SetStatus("Не удалось изменить статус цели", error.Message, isError: true);
        }
    }

    private async Task ClearThreadGoalAsync()
    {
        var thread = _currentThread;
        if (thread is null)
        {
            SetStatus("Цель недоступна", "Сначала выберите чат.");
            return;
        }
        if (thread.ActiveRun is not null)
        {
            SetStatus("Цель сейчас недоступна", "Дождитесь завершения текущего хода Codex.");
            return;
        }
        if (string.IsNullOrWhiteSpace(thread.CodexSessionId) && thread.PendingGoal is not null)
        {
            var answer = CodexMessageDialog.Show(this, "Удалить черновик цели этого чата?", "Очистить цель",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            var pending = thread.PendingGoal;
            thread.PendingGoal = null;
            if (!await SaveStateAsync())
            {
                thread.PendingGoal = pending;
                SetStatus("Не удалось удалить черновик цели", _startupIssue ?? "Не удалось сохранить состояние чата.", isError: true);
                return;
            }
            UpdateThreadGoalButton();
            SetStatus("Черновик цели удалён", "Изменение сохранено в CodexGUI на этом компьютере.");
            return;
        }
        if (thread.CodexSessionId is not { Length: > 0 } threadId)
        {
            SetStatus("Цель недоступна", "Сначала отправьте первый запрос, чтобы создать сессию чата.");
            return;
        }
        try
        {
            var client = new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token);
            var hasPending = thread.PendingGoal is not null;
            var current = hasPending ? null : ReadThreadGoal(await client.ReadThreadGoalAsync(threadId));
            if (current is null && !hasPending)
            {
                _threadGoalCache[threadId] = null;
                UpdateThreadGoalButton();
                SetStatus("Цель ещё не задана", "В этой сессии нет сохранённой цели.");
                return;
            }
            var answer = CodexMessageDialog.Show(this, "Удалить цель этого чата?", "Очистить цель",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            var result = await client.ClearThreadGoalAsync(threadId);
            var cleared = result.TryGetProperty("cleared", out var value) && value.ValueKind == JsonValueKind.True;
            thread.PendingGoal = null;
            _threadGoalCache[threadId] = null;
            _threadGoalLoadFailures.Remove(threadId);
            RequestStateSave();
            UpdateThreadGoalButton();
            SetStatus(cleared || hasPending ? "Цель очищена" : "Цели уже нет", "Изменение сохранено в истории App Server.");
        }
        catch (Exception error)
        {
            if (!_isClosing) SetStatus("Не удалось очистить цель чата", error.Message, isError: true);
        }
    }

    private static CodexThreadGoal? ReadThreadGoal(JsonElement response)
    {
        if (!response.TryGetProperty("goal", out var goal) || goal.ValueKind != JsonValueKind.Object)
            return null;
        ulong? tokenBudget = goal.TryGetProperty("tokenBudget", out var budget)
            && budget.ValueKind == JsonValueKind.Number && budget.TryGetUInt64(out var parsedBudget)
                ? parsedBudget
                : null;
        var tokensUsed = goal.TryGetProperty("tokensUsed", out var tokens)
            && tokens.ValueKind == JsonValueKind.Number && tokens.TryGetUInt64(out var parsedTokens)
                ? parsedTokens
                : 0;
        var timeUsedSeconds = goal.TryGetProperty("timeUsedSeconds", out var duration)
            && duration.ValueKind == JsonValueKind.Number && duration.TryGetUInt64(out var parsedDuration)
                ? parsedDuration
                : 0;
        return new CodexThreadGoal(GetString(goal, "objective") ?? "", GetString(goal, "status") ?? "active",
            tokenBudget, tokensUsed, timeUsedSeconds);
    }

    private static string ThreadGoalStatusLabel(string status) => status switch
    {
        "active" => "активна",
        "paused" => "приостановлена",
        "blocked" => "заблокирована",
        "usageLimited" => "лимит использования",
        "budgetLimited" => "лимит бюджета",
        "complete" => "завершена",
        _ => status
    };

    private async Task RefreshRepositoryInfoAsync()
    {
        if (_isClosing) return;
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
                if (_isClosing) return;
                if (!string.Equals(projectPath, _projectPath, StringComparison.OrdinalIgnoreCase))
                {
                    _repositoryRefreshPending = true;
                    continue;
                }

                _repositorySnapshot = snapshot;
                _snapshotProjectPath = projectPath;
                RenderRepositoryInfo(snapshot);
                UpdateChatQuickOverview();
            }
            while (_repositoryRefreshPending && !_isClosing);
        }
        catch (OperationCanceledException) when (_isClosing) { }
        finally
        {
            _isRefreshingRepository = false;
            if (_repositoryRefreshPending && !_isClosing)
                _ = RefreshRepositoryInfoAsync();
        }
    }

    private void RenderRepositoryInfo(GitRepositorySnapshot snapshot)
    {
        if (Control<TextBlock>("RepositoryBranch") is not { } branch ||
            Control<TextBlock>("RepositorySummary") is not { } summary ||
            Control<StackPanel>("RepositoryChangesPanel") is not { } changesPanel ||
            Control<Button>("RepositoryRemoteButton") is not { } remoteButton ||
            Control<Button>("ReviewChangesButton") is not { } reviewButton) return;

        changesPanel.Children.Clear();
        remoteButton.Visibility = snapshot.RemoteUrl is null ? Visibility.Collapsed : Visibility.Visible;
        remoteButton.ToolTip = snapshot.RemoteUrl;
        reviewButton.Visibility = Visibility.Collapsed;
        if (!snapshot.IsRepository)
        {
            branch.Text = snapshot.Error ?? "Git-репозиторий не найден";
            summary.Text = "Нет данных о состоянии репозитория";
            UpdateGitActionButtons();
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

        AddRepositoryFileGroup(changesPanel, "В ИНДЕКСЕ", staged, snapshot.Root, RepositoryDiffScope.Staged);
        AddRepositoryFileGroup(changesPanel, "ИЗМЕНЕНЫ", modified, snapshot.Root, RepositoryDiffScope.Unstaged);
        AddRepositoryFileGroup(changesPanel, "НЕ ОТСЛЕЖИВАЮТСЯ", untracked, snapshot.Root, RepositoryDiffScope.Unstaged);
        reviewButton.Visibility = snapshot.Changes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateGitActionButtons();
        UpdateReviewChangesButton();
        if (snapshot.Changes.Count == 0)
            changesPanel.Children.Add(new TextBlock { Text = "Рабочее дерево чистое", FontSize = 10, Foreground = Brush("#777980"), Margin = new Thickness(0, 5, 0, 2) });
    }

    private void UpdateReviewChangesButton()
    {
        UpdateGitActionButtons();
        if (Control<Button>("ReviewChangesButton") is not { } button) return;
        button.IsEnabled = !_isReviewingChanges && !_isGitCommandRunning && !HasActiveRuns
            && _repositorySnapshot is { IsRepository: true, Changes.Count: > 0 };
        button.Content = _isReviewingChanges ? "Codex проверяет…" : "Проверить изменения";
    }

    private void UpdateGitActionButtons()
    {
        var snapshot = _repositorySnapshot;
        var isRepository = snapshot is { IsRepository: true };
        var stagedCount = snapshot?.Changes.Count(file => !file.IsUntracked && file.IndexStatus != '.') ?? 0;
        var unstagedCount = snapshot?.Changes.Count(file => file.IsUntracked || file.WorktreeStatus != '.') ?? 0;
        var canMutate = isRepository && !_isGitCommandRunning && !_isReviewingChanges && !HasActiveRuns;

        if (Control<Button>("StageAllChangesButton") is { } stageAll)
        {
            stageAll.Visibility = unstagedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            stageAll.IsEnabled = canMutate;
        }
        if (Control<Button>("UnstageAllChangesButton") is { } unstageAll)
        {
            unstageAll.Visibility = stagedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            unstageAll.IsEnabled = canMutate;
        }
        if (Control<StackPanel>("RepositoryCommitPanel") is { } commitPanel)
            commitPanel.Visibility = stagedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Control<Button>("PushBranchButton") is { } push)
        {
            var publishNewBranch = isRepository && snapshot!.HasHead && snapshot.HasOrigin
                && snapshot.Upstream is null && snapshot.Branch != "(detached)";
            var pushAhead = isRepository && snapshot!.HasHead && snapshot.Upstream is not null && snapshot.Ahead > 0;
            push.Visibility = publishNewBranch || pushAhead ? Visibility.Visible : Visibility.Collapsed;
            push.Content = publishNewBranch ? "Опубликовать ветку" : $"Отправить ↑{snapshot?.Ahead ?? 0}";
            push.IsEnabled = canMutate;
            push.ToolTip = publishNewBranch
                ? "Опубликовать текущую ветку в origin и настроить upstream"
                : "Отправить текущую ветку в настроенный upstream без force-push";
        }
        if (Control<Button>("CreatePullRequestButton") is { } createPullRequest)
        {
            var isGitHubRepository = GitHubPullRequestService.TryGetRepositorySlug(snapshot?.RemoteUrl, out _);
            var branchPublished = isRepository && snapshot!.HasHead && snapshot.Branch != "(detached)"
                && snapshot.Upstream?.StartsWith("origin/", StringComparison.Ordinal) == true
                && snapshot.Ahead == 0;
            createPullRequest.Visibility = isGitHubRepository && isRepository && snapshot!.HasHead
                && snapshot.Branch != "(detached)" ? Visibility.Visible : Visibility.Collapsed;
            createPullRequest.IsEnabled = canMutate && branchPublished && snapshot!.Changes.Count == 0;
            createPullRequest.ToolTip = !branchPublished
                ? "Сначала опубликуйте текущую ветку в origin"
                : snapshot!.Changes.Count > 0
                    ? "Перед созданием PR закоммитьте или отмените незакоммиченные изменения"
                    : "Создать Pull Request для отправленной ветки; GitHub CLI должен быть установлен и авторизован";
        }
        if (Control<TextBox>("CommitMessageInput") is { } message)
            message.IsEnabled = canMutate && stagedCount > 0;
        if (Control<Button>("CommitStagedButton") is { } commit)
            commit.IsEnabled = canMutate && stagedCount > 0
                && !string.IsNullOrWhiteSpace(Control<TextBox>("CommitMessageInput")?.Text);
    }

    private async Task<bool> RunRepositoryGitCommandAsync(string progress, string success,
        Func<string, CancellationToken, Task> command)
    {
        if (_isGitCommandRunning || _isReviewingChanges || HasActiveRuns
            || _repositorySnapshot is not { IsRepository: true } snapshot)
            return false;

        _isGitCommandRunning = true;
        UpdateGitActionButtons();
        UpdateReviewChangesButton();
        SetStatus(progress, snapshot.Root);
        try
        {
            await command(snapshot.Root, _repositoryRefreshCancellation.Token);
            if (_isClosing) return false;
            await RefreshRepositoryInfoAsync();
            SetStatus(success, snapshot.Root);
            return true;
        }
        catch (OperationCanceledException) when (_isClosing) { return false; }
        catch (Exception error)
        {
            if (!_isClosing) SetStatus("Git-действие не выполнено", error.Message, isError: true);
            return false;
        }
        finally
        {
            _isGitCommandRunning = false;
            UpdateGitActionButtons();
            UpdateReviewChangesButton();
        }
    }

    private async Task CommitStagedChangesAsync()
    {
        var input = Control<TextBox>("CommitMessageInput");
        var message = input?.Text.Trim();
        if (string.IsNullOrWhiteSpace(message)) return;
        var committed = await RunRepositoryGitCommandAsync("Создаю коммит…", "Коммит создан.",
            async (root, token) => { _ = await GitRepositoryInspector.CommitStagedAsync(root, message, token); });
        if (committed && input is not null) input.Clear();
    }

    private async Task CreatePullRequestAsync()
    {
        if (_repositorySnapshot is not { IsRepository: true } snapshot
            || !GitHubPullRequestService.TryGetRepositorySlug(snapshot.RemoteUrl, out var repositorySlug)
            || snapshot.Upstream is null || !snapshot.Upstream.StartsWith("origin/", StringComparison.Ordinal)
            || snapshot.Ahead > 0 || snapshot.Changes.Count > 0 || snapshot.Branch == "(detached)") return;

        var editor = new CodexPullRequestWindow(snapshot.Branch) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } draft) return;

        string? pullRequestUrl = null;
        var created = await RunRepositoryGitCommandAsync("Создаю Pull Request…", "Pull Request создан.", async (root, token) =>
        {
            pullRequestUrl = await GitHubPullRequestService.CreateAsync(root, repositorySlug,
                snapshot.Branch, draft.Title, draft.Body, draft.IsDraft, token);
        });
        if (!created || pullRequestUrl is null || _isClosing) return;

        try
        {
            Process.Start(new ProcessStartInfo(pullRequestUrl) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus("Pull Request создан", $"Не удалось открыть браузер: {error.Message} · {pullRequestUrl}");
        }
    }

    private async Task PushCurrentBranchAsync()
    {
        if (_repositorySnapshot is not { IsRepository: true } snapshot) return;
        var publishNewBranch = snapshot.Upstream is null;
        var success = publishNewBranch ? "Ветка опубликована в origin." : "Ветка отправлена в настроенный upstream.";
        await RunRepositoryGitCommandAsync(publishNewBranch ? "Публикую ветку…" : "Отправляю ветку…", success,
            (root, token) => GitRepositoryInspector.PushCurrentBranchAsync(root, publishNewBranch, token));
    }

    private async Task ReviewUncommittedChangesAsync()
    {
        if (_isReviewingChanges || _isGitCommandRunning || HasActiveRuns
            || _repositorySnapshot is not { IsRepository: true, Changes.Count: > 0 } snapshot) return;
        _isReviewingChanges = true;
        UpdateGitActionButtons();
        UpdateReviewChangesButton();
        SetStatus("Проверяю изменения…", $"Codex анализирует незакоммиченные изменения в {snapshot.Root}.");
        try
        {
            var findings = await ReviewUncommittedWithAppServerAsync(snapshot.Root, _repositoryRefreshCancellation.Token);
            if (_isClosing) return;
            ShowCodeReviewResults(snapshot.Root, findings);
            SetStatus("Проверка завершена", string.IsNullOrWhiteSpace(findings) ? "Codex не сообщил замечаний." : $"Проверены изменения в {Path.GetFileName(snapshot.Root)}.");
        }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception error)
        {
            if (!_isClosing) SetStatus("Проверка изменений не удалась", error.Message, isError: true);
        }
        finally
        {
            _isReviewingChanges = false;
            UpdateGitActionButtons();
            UpdateReviewChangesButton();
        }
    }

    private async Task<string> ReviewUncommittedWithAppServerAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        await using var connection = await CodexAppServerConnection.StartAsync(CodexAppServerRuntime.ResolveExecutable());
        string? threadId = null;
        string? turnId = null;
        string? status = null;
        try
        {
            await connection.InitializeAsync();
            var threadResult = await connection.SendRequestAsync("thread/start", new
            {
                cwd = repositoryPath,
                model = _selectedModel,
                approvalPolicy = "on-request",
                sandbox = "read-only",
                config = new { model_reasoning_effort = _selectedEffort }
            });
            threadId = threadResult.TryGetProperty("thread", out var thread) ? GetString(thread, "id") : null;
            if (string.IsNullOrWhiteSpace(threadId))
                throw new InvalidDataException("Codex App Server не создал временный чат для review.");

            var reviewResult = await connection.SendRequestAsync("review/start", new
            {
                threadId,
                delivery = "inline",
                target = new { type = "uncommittedChanges" }
            });
            turnId = reviewResult.TryGetProperty("turn", out var turn) ? GetString(turn, "id") : null;
            if (string.IsNullOrWhiteSpace(turnId))
                throw new InvalidDataException("Codex App Server не вернул идентификатор review-хода.");

            string? findings = null;
            string? failure = null;
            while (status is null)
            {
                while (connection.TryReadIncoming(out var message))
                {
                    if (!message.TryGetProperty("method", out var methodValue)) continue;
                    var method = methodValue.GetString() ?? "";
                    if (message.TryGetProperty("id", out var requestId))
                    {
                        if ((method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
                            && message.TryGetProperty("params", out var approvalParameters))
                        {
                            ShowFromTray();
                            var decision = ShowAppServerApprovalDialog(method, approvalParameters);
                            await connection.SendResponseAsync(requestId, new { decision });
                        }
                        else if ((method is "item/tool/requestUserInput" or "mcpServer/elicitation/request" or "item/permissions/requestApproval")
                            && message.TryGetProperty("params", out var requestParameters))
                        {
                            ShowFromTray();
                            string responseJson;
                            try
                            {
                                responseJson = CodexAppServerRequestDialog.Show(this,
                                    "review:" + requestId.GetRawText(), method, requestParameters.Clone());
                            }
                            catch (Exception displayError)
                            {
                                SetStatus("Не удалось показать запрос Codex", displayError.Message, isError: true);
                                responseJson = CodexAppServerRequestDialog.BuildSafeCancellationResponse(method, requestParameters);
                            }
                            using var responseDocument = JsonDocument.Parse(responseJson);
                            await connection.SendResponseAsync(requestId, responseDocument.RootElement);
                        }
                        else
                            await connection.SendErrorAsync(requestId, -32601, "CodexGUI does not support this review request yet.");
                        continue;
                    }

                    if (!message.TryGetProperty("params", out var parameters)) continue;
                    if (method == "error" && parameters.TryGetProperty("error", out var error))
                        failure = GetString(error, "message");
                    else if (method == "item/completed" && parameters.TryGetProperty("item", out var item)
                        && GetString(item, "type") == "exitedReviewMode")
                        findings = GetString(item, "review");
                    else if (method == "turn/completed" && parameters.TryGetProperty("turn", out var completedTurn)
                        && GetString(completedTurn, "id") == turnId)
                        status = GetString(completedTurn, "status") ?? "failed";
                }

                if (status is not null) break;
                if (connection.ReaderTask.IsCompleted)
                    throw new EndOfStreamException("Codex App Server завершился до окончания review.");
                await Task.Delay(50, cancellationToken);
            }

            if (status != "completed")
                throw new InvalidOperationException(failure ?? $"Codex review завершился со статусом {status}.");
            return findings ?? "Codex не вернул текст результата review.";
        }
        finally
        {
            if (threadId is { Length: > 0 })
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (status is null && turnId is { Length: > 0 })
                {
                    try { await connection.SendRequestAsync("turn/interrupt", new { threadId, turnId }, cleanup.Token); }
                    catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException) { }
                }
                try { await connection.SendRequestAsync("thread/delete", new { threadId }, cleanup.Token); }
                catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException) { }
            }
        }
    }

    private void ShowCodeReviewResults(string repositoryPath, string findings)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = $"Проверка изменений · {Path.GetFileName(repositoryPath)}",
            Width = 820,
            Height = 660,
            MinWidth = 560,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#101114"),
            Foreground = Brush("#F1F3F5")
        };
        CodexWindowTheme.Apply(dialog);
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock
        {
            Text = repositoryPath,
            Foreground = Brush("#A4B1C2"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var output = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(findings) ? "Codex не сообщил замечаний." : findings,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(12),
            Background = Brush("#1D1F23"),
            Foreground = Brush("#D2D9E3"),
            BorderBrush = Brush("#34373E"),
            BorderThickness = new Thickness(1)
        };
        Grid.SetRow(output, 1);
        layout.Children.Add(output);
        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 7, 14, 7),
            Background = Brush("#263344"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        close.Click += (_, _) => dialog.Close();
        Grid.SetRow(close, 2);
        layout.Children.Add(close);
        dialog.Content = layout;
        dialog.ShowDialog();
    }

    private void AddRepositoryFileGroup(StackPanel panel, string heading, IReadOnlyList<RepositoryFileStatus> files,
        string repositoryRoot, RepositoryDiffScope diffScope)
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
                ToolTip = $"Левый клик — diff; правый клик — Git-действия для {file.Path}"
            };
            button.Click += async (_, _) => await ShowRepositoryFileDiffAsync(repositoryRoot, file, diffScope);
            var contextMenu = new ContextMenu();
            if (file.IsUntracked || file.WorktreeStatus != '.')
            {
                var stage = new MenuItem { Header = "Добавить изменения в индекс" };
                stage.Click += async (_, _) => await RunRepositoryGitCommandAsync(
                    "Добавляю файл в индекс…", $"Изменения файла «{file.Path}» добавлены в индекс.",
                    (root, token) => GitRepositoryInspector.StageFileAsync(root, file.Path, token));
                contextMenu.Items.Add(stage);
            }
            if (file.IsUntracked)
            {
                var removeUntracked = new MenuItem { Header = "Удалить неотслеживаемые файлы…" };
                removeUntracked.Click += async (_, _) => await PreviewAndRemoveUntrackedPathAsync(repositoryRoot, file.Path);
                contextMenu.Items.Add(removeUntracked);
            }
            if (!file.IsUntracked && file.IndexStatus != '.')
            {
                var unstage = new MenuItem { Header = "Убрать из индекса" };
                unstage.Click += async (_, _) => await RunRepositoryGitCommandAsync(
                    "Убираю файл из индекса…", $"Файл «{file.Path}» убран из индекса; изменения сохранены.",
                    (root, token) => GitRepositoryInspector.UnstageFileAsync(root, file.Path, token));
                contextMenu.Items.Add(unstage);
            }
            if (!file.IsUntracked)
            {
                if (contextMenu.Items.Count > 0) contextMenu.Items.Add(new Separator());
                var restore = new MenuItem { Header = "Отменить все изменения файла…" };
                restore.Click += async (_, _) =>
                {
                    var answer = CodexMessageDialog.Show(this,
                        $"Отменить staged- и unstaged-изменения файла «{file.Path}» и восстановить версию из HEAD? Это действие удалит изменения этого файла.",
                        "Отменить изменения файла", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) return;
                    await RunRepositoryGitCommandAsync("Восстанавливаю файл из HEAD…", $"Файл «{file.Path}» восстановлен из HEAD.",
                        (root, token) => GitRepositoryInspector.RestoreFileFromHeadAsync(root, file.Path, token));
                };
                contextMenu.Items.Add(restore);
            }
            contextMenu.Opened += (_, _) =>
            {
                var canMutate = !_isGitCommandRunning && !_isReviewingChanges && !HasActiveRuns;
                foreach (var item in contextMenu.Items.OfType<MenuItem>()) item.IsEnabled = canMutate;
            };
            if (contextMenu.Items.Count > 0) button.ContextMenu = contextMenu;
            panel.Children.Add(button);
        }

        if (files.Count > 8)
            panel.Children.Add(new TextBlock { Text = $"и ещё {files.Count - 8}…", FontSize = 10, Foreground = Brush("#777980"), Margin = new Thickness(2, 2, 0, 0) });
    }

    private async Task PreviewAndRemoveUntrackedPathAsync(string repositoryRoot, string relativePath)
    {
        if (_isGitCommandRunning || _isReviewingChanges || HasActiveRuns
            || _repositorySnapshot is not { IsRepository: true } snapshot
            || !string.Equals(Path.GetFullPath(snapshot.Root), Path.GetFullPath(repositoryRoot), StringComparison.OrdinalIgnoreCase))
            return;

        _isGitCommandRunning = true;
        UpdateGitActionButtons();
        UpdateReviewChangesButton();
        try
        {
            var preview = await GitRepositoryInspector.PreviewUntrackedRemovalAsync(
                snapshot.Root, relativePath, _repositoryRefreshCancellation.Token);
            if (_isClosing) return;
            if (string.IsNullOrWhiteSpace(preview))
            {
                SetStatus("Нет неотслеживаемых файлов для удаления", relativePath);
                return;
            }

            var paths = preview.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.StartsWith("Would remove ", StringComparison.Ordinal)
                    ? line["Would remove ".Length..]
                    : line)
                .ToArray();
            var visiblePaths = string.Join(Environment.NewLine, paths.Take(20));
            if (paths.Length > 20) visiblePaths += $"{Environment.NewLine}… и ещё {paths.Length - 20}";
            var answer = CodexMessageDialog.Show(this,
                $"Git удалит неотслеживаемые файлы по пути «{relativePath}». Игнорируемые файлы и файлы из индекса не удаляются. Продолжить?\n\n{visiblePaths}",
                "Удалить неотслеживаемые файлы", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes || _isClosing || HasActiveRuns) return;

            var confirmedPreview = await GitRepositoryInspector.PreviewUntrackedRemovalAsync(
                snapshot.Root, relativePath, _repositoryRefreshCancellation.Token);
            if (!string.Equals(preview, confirmedPreview, StringComparison.Ordinal))
            {
                SetStatus("Список файлов изменился", "Обновите состояние Git и повторите удаление.", isError: true);
                return;
            }
            if (HasActiveRuns || _repositorySnapshot is not { IsRepository: true }
                || !string.Equals(Path.GetFullPath(_repositorySnapshot.Root), Path.GetFullPath(snapshot.Root), StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Удаление отменено", "Репозиторий изменился или Codex начал работу.");
                return;
            }

            await GitRepositoryInspector.RemoveUntrackedPathAsync(snapshot.Root, relativePath, _repositoryRefreshCancellation.Token);
            if (_isClosing) return;
            await RefreshRepositoryInfoAsync();
            SetStatus("Неотслеживаемые файлы удалены", relativePath);
        }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception error)
        {
            if (!_isClosing) SetStatus("Не удалось удалить неотслеживаемые файлы", error.Message, isError: true);
        }
        finally
        {
            _isGitCommandRunning = false;
            UpdateGitActionButtons();
            UpdateReviewChangesButton();
        }
    }

    private async Task ShowRepositoryFileDiffAsync(string repositoryRoot, RepositoryFileStatus file, RepositoryDiffScope diffScope)
    {
        try
        {
            var diff = await GitRepositoryInspector.ReadFileDiffAsync(repositoryRoot, file, diffScope,
                _repositoryRefreshCancellation.Token);
            if (_isClosing) return;
            if (string.IsNullOrWhiteSpace(diff))
            {
                SetStatus("Изменений в файле нет", file.Path);
                return;
            }
            ShowFileDiff(repositoryRoot, file.Path, diff, diffScope, hunkActionsAvailable: !file.IsUntracked);
        }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception error)
        {
            if (!_isClosing) SetStatus("Не удалось открыть diff", error.Message, isError: true);
        }
    }

    private void ShowFileDiff(string repositoryRoot, string relativePath, string diff, RepositoryDiffScope diffScope,
        bool hunkActionsAvailable)
    {
        var dialog = new DiffReviewerWindow(this, repositoryRoot, relativePath, diff, diffScope, hunkActionsAvailable,
            (patch, action) => ApplyDiffHunkAsync(repositoryRoot, relativePath, patch, action),
            threads => AppendDiffCommentThreads(relativePath, threads));
        if (dialog.ShowDialog() == true) Control<TextBox>("PromptInput")?.Focus();
    }

    private async Task<bool> ApplyDiffHunkAsync(string repositoryRoot, string relativePath, string patch, GitHunkAction action)
    {
        if (_repositorySnapshot is not { IsRepository: true } snapshot
            || !string.Equals(Path.GetFullPath(snapshot.Root), Path.GetFullPath(repositoryRoot), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Репозиторий изменился", "Обновите состояние Git и откройте diff заново.", isError: true);
            return false;
        }

        var (progress, success) = action switch
        {
            GitHunkAction.Stage => ("Добавляю блок изменений в индекс…", "Блок изменений добавлен в индекс."),
            GitHunkAction.Unstage => ("Убираю блок из индекса…", "Блок убран из индекса; файлы сохранены."),
            _ => ("Отменяю незастейдженный блок…", "Незастейдженный блок отменён.")
        };
        return await RunRepositoryGitCommandAsync(progress, success,
            (root, token) => GitRepositoryInspector.ApplyDiffHunkAsync(root, relativePath, patch, action, token));
    }

    private bool AppendDiffCommentThreads(string relativePath, IReadOnlyList<DiffCommentThread> threads)
    {
        if (threads.Count == 0) return false;
        var input = Control<TextBox>("PromptInput");
        if (input is null) return false;
        if (_currentThread is null && _selectedProject is not null)
        {
            _currentThread = NewThread();
            RenderThreads();
            RenderConversation();
            UpdateChatInfoPanel();
        }
        if (input.Text == PromptPlaceholder) input.Clear();
        var note = new StringBuilder($"Ветки замечаний к изменениям в файле `{relativePath}`:\n\n");
        for (var index = 0; index < threads.Count; index++)
        {
            var thread = threads[index];
            note.Append($"### Замечание {index + 1}");
            if (!string.IsNullOrWhiteSpace(thread.LineAnchor)) note.Append($" ({thread.LineAnchor})");
            note.AppendLine();
            note.AppendLine(thread.Comment);
            foreach (var reply in thread.Replies) note.Append("Ответ: ").AppendLine(reply);
            note.AppendLine("```diff");
            note.AppendLine(thread.SelectedDiff.TrimEnd());
            note.AppendLine("```");
            note.AppendLine();
        }
        input.Text = (string.IsNullOrWhiteSpace(input.Text) ? "" : input.Text.TrimEnd() + "\n\n") + note;
        input.CaretIndex = input.Text.Length;
        return true;
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
            heading.Text = $"Очередь · {_promptQueue.Count}";
        if (Control<Button>("ClearPromptQueueButton") is { } clearButton)
            clearButton.IsEnabled = _promptQueue.Count > 0;

        var position = 0;
        foreach (var queued in _promptQueue)
        {
            var threadTitle = _threads.FirstOrDefault(thread => thread.Id == queued.ThreadId)?.Title;
            var (messageText, attachments) = SplitPromptWithAttachments(queued.Text);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Border
            {
                Background = Brush("#29453F"),
                CornerRadius = new CornerRadius(6),
                MinWidth = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = (++position).ToString(),
                    FontSize = 10,
                    Foreground = Brush("#92E5C7"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            var summary = string.Join(" ", messageText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (attachments.Length > 0)
                summary += $"  · Вложения: {attachments.Length}";
            var details = new TextBlock
            {
                Text = summary.Trim(), FontSize = 11,
                Foreground = Brush("#F1F3F5"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = $"{threadTitle ?? "Чат"}\n{queued.Text}"
            };
            Grid.SetColumn(details, 1);
            row.Children.Add(details);
            var edit = CreateThreadIconButton("Редактировать сообщение",
                "M3,17 L3,21 L7,21 L20,8 L16,4 Z M13,7 L17,11", "#AFC5D3");
            edit.VerticalAlignment = VerticalAlignment.Center;
            edit.Click += (_, _) => EditQueuedPrompt(queued);
            Grid.SetColumn(edit, 2);
            row.Children.Add(edit);
            var remove = CreateThreadIconButton("Убрать из очереди", "M5,5 L19,19 M19,5 L5,19", "#B0B5BE");
            remove.VerticalAlignment = VerticalAlignment.Center;
            remove.Click += (_, _) => RemovePromptFromQueue(queued.Id);
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);
            list.Children.Add(new Border
            {
                Background = Brush("#24272D"),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1, 3, 1),
                Margin = new Thickness(0, 2, 0, 0),
                Child = row
            });
        }

        UpdateChatQuickOverview();
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
        var activeRun = _currentThread?.ActiveRun;
        var canControlTurn = activeRun is { AppServerMode: true };
        if (Control<TextBlock>("SendButtonIcon") is { } icon)
            icon.Text = canControlTurn && activeRun?.SteerFollowUps == true ? "↑" : queueing ? "＋" : "↑";
        if (Control<Button>("SendButton") is { } sendButton && canControlTurn)
            sendButton.ToolTip = activeRun?.SteerFollowUps == true ? "Направить в текущий ход (Enter)" : "Добавить в очередь (Enter)";
        if (Control<Button>("FollowUpModeButton") is { } followUpMode)
        {
            followUpMode.Visibility = canControlTurn ? Visibility.Visible : Visibility.Collapsed;
            followUpMode.Content = activeRun?.SteerFollowUps == true ? "Направить" : "В очередь";
            followUpMode.ToolTip = activeRun?.SteerFollowUps == true
                ? "Отправить следующее сообщение прямо в текущий ход Codex"
                : "Поставить сообщение в очередь после текущего хода Codex";
            followUpMode.IsEnabled = canControlTurn && activeRun?.StopRequested != true;
        }
        if (Control<Button>("StopButton") is { } stop)
        {
            stop.Visibility = canControlTurn ? Visibility.Visible : Visibility.Collapsed;
            stop.IsEnabled = canControlTurn && activeRun?.StopRequested != true;
            stop.Content = activeRun?.StopRequested == true ? "Останавливаю…" : "Остановить";
        }
        UpdateWorktreeButton();
        UpdateReviewChangesButton();
    }

    private void ToggleFollowUpMode()
    {
        if (_currentThread?.ActiveRun is not { AppServerMode: true } run) return;
        run.SteerFollowUps = !run.SteerFollowUps;
        RequestStateSave();
        UpdateSendButtonState();
    }

    private async Task InterruptCurrentTurnAsync()
    {
        if (_currentThread?.ActiveRun is not { AppServerMode: true, StopRequested: false } run) return;
        run.StopRequested = true;
        RequestStateSave();
        UpdateSendButtonState();
        try
        {
            await WriteAppServerControlAsync(run, new CodexAppServerWorkerControl { Action = "interrupt" });
            SetStatus("Останавливаю Codex…", "Запрос на прерывание отправлен.");
        }
        catch (Exception error)
        {
            run.StopRequested = false;
            RequestStateSave();
            UpdateSendButtonState();
            SetStatus("Не удалось остановить Codex", error.Message, isError: true);
        }
    }

    private static async Task WriteAppServerControlAsync(ActiveCliRun run, CodexAppServerWorkerControl control)
    {
        var controlDirectory = Path.Combine(Path.GetDirectoryName(run.CompletionPath)!, "controls");
        Directory.CreateDirectory(controlDirectory);
        var controlPath = Path.Combine(controlDirectory, $"{DateTime.UtcNow.Ticks:D20}_{Guid.NewGuid():N}.json");
        var temporaryPath = controlPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(control), new UTF8Encoding(false));
        File.Move(temporaryPath, controlPath);
    }

    private void UpdateWorktreeButton()
    {
        if (Control<Button>("WorktreeButton") is not { } button) return;
        var enabled = _currentThread?.UseWorktree == true;
        if (Control<TextBlock>("WorktreeLabel") is { } label)
            label.Text = enabled ? "Изолированная копия" : "Рабочая копия";
        button.Background = Brush(enabled ? "#223B38" : "#282B30");
        button.Foreground = Brush(enabled ? "#92E5C7" : "#C8D4E2");
        button.IsEnabled = _currentThread?.ActiveRun is null
            && !(_currentThread?.UseWorktree == true && _currentThread.CodexSessionId is not null)
            && !_isClosing;
        button.ToolTip = enabled
            ? _currentThread?.CodexSessionId is not null
                ? "Этот чат уже продолжает отдельную Git-копию. Создайте новый чат, если хотите выбрать другую среду."
                : "Для этого чата включён отдельный Git worktree."
            : "Вести запросы этого чата в отдельном изолированном Git worktree.";
    }

    private async Task ToggleWorktreeModeAsync()
    {
        var thread = _currentThread;
        if (thread?.ActiveRun is not null) return;
        if (thread?.UseWorktree == true && thread.CodexSessionId is not null) return;
        if (thread?.UseWorktree != true)
        {
            var projectPath = thread?.ProjectPath ?? _selectedProject?.Path;
            if (projectPath is null) return;
            var repository = await GitRepositoryInspector.ReadAsync(projectPath, CancellationToken.None);
            if (!repository.IsRepository)
            {
                CodexMessageDialog.Show(this, "Для отдельной рабочей копии выберите Git-репозиторий.", "Рабочая копия", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        if (thread?.CodexSessionId is { Length: > 0 } sessionId)
        {
            var fork = new ChatThread
            {
                Title = "Копия: " + TrimTitle(thread.Title),
                ProjectId = thread.ProjectId,
                ProjectPath = thread.ProjectPath,
                CodexSessionId = sessionId,
                ForkFromSessionId = sessionId,
                UseWorktree = true
            };
            _threads.Add(fork);
            thread = _currentThread = fork;
        }
        else
        {
            thread ??= _selectedProject is null ? null : NewThread();
            if (thread is null) return;
            thread.UseWorktree = !thread.UseWorktree;
        }

        RequestStateSave();
        RenderThreads();
        RenderConversation();
        UpdateChatInfoPanel();
        if (thread.CodexSessionId is not null)
            await RestoreThreadHistoryAsync(thread);
        UpdateWorktreeButton();
        SetStatus("Изолированная копия включена", thread.CodexSessionId is null
            ? "Первый запрос создаст отдельный Git worktree."
            : "Ответвлённый чат продолжится в отдельном Git worktree.");
    }

    private void EditQueuedPrompt(QueuedPrompt queued)
    {
        if (_isEditingQueuedPrompt || !_promptQueue.Contains(queued)) return;

        var (messageText, attachments) = SplitPromptWithAttachments(queued.Text);
        var dialog = new Window
        {
            Owner = this,
            Title = "Редактировать сообщение в очереди",
            Width = 620,
            Height = 520,
            MinWidth = 460,
            MinHeight = 330,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Background = Brush("#1D1F23"),
            Foreground = Brush("#F1F3F5")
        };
        CodexWindowTheme.Apply(dialog);
        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(new TextBlock
        {
            Text = "Текст сообщения",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var input = new TextBox
        {
            Text = messageText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 250,
            MinHeight = 180,
            MaxHeight = 270,
            Padding = new Thickness(10, 8, 10, 8),
            Background = Brush("#202D39"),
            Foreground = Brush("#EAF3F6"),
            BorderBrush = Brush("#405869"),
            BorderThickness = new Thickness(1),
            FontSize = 13
        };
        layout.Children.Add(input);

        if (attachments.Length > 0)
        {
            layout.Children.Add(new TextBlock
            {
                Text = $"Вложения сохранятся · {attachments.Length}",
                FontSize = 10,
                Foreground = Brush("#9FB2C3"),
                Margin = new Thickness(0, 11, 0, 5)
            });
            var attachmentPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var path in attachments)
            {
                attachmentPanel.Children.Add(new Border
                {
                    Background = Brush("#263A49"),
                    BorderBrush = Brush("#405869"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 3, 7, 3),
                    Margin = new Thickness(0, 0, 5, 4),
                    ToolTip = path,
                    Child = new TextBlock
                    {
                        Text = Path.GetFileName(path),
                        FontSize = 10,
                        Foreground = Brush("#DCE7ED"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 220
                    }
                });
            }
            layout.Children.Add(new ScrollViewer
            {
                Content = attachmentPanel,
                MaxHeight = 66,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button
        {
            Content = "Отмена",
            IsCancel = true,
            MinWidth = 84,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = Brush("#26333F"),
            Foreground = Brush("#DCE7ED")
        };
        var save = new Button
        {
            Content = "Сохранить",
            MinWidth = 100,
            Padding = new Thickness(10, 6, 10, 6),
            Background = Brush("#92E5C7"),
            Foreground = Brush("#121D27")
        };
        save.Click += (_, _) => dialog.DialogResult = true;
        input.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(input.Text) || attachments.Length > 0;
        save.IsEnabled = !string.IsNullOrWhiteSpace(input.Text) || attachments.Length > 0;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        layout.Children.Add(buttons);
        dialog.Content = layout;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.CaretIndex = input.Text.Length;
        };

        _isEditingQueuedPrompt = true;
        try
        {
            if (dialog.ShowDialog() != true || !_promptQueue.Contains(queued)) return;
            queued.Text = FormatPromptWithAttachments(input.Text.Trim(), attachments);
            RequestStateSave();
            RenderPromptQueue();
        }
        finally
        {
            _isEditingQueuedPrompt = false;
            _ = ProcessQueuedPromptsAsync();
        }
    }

    private void RemovePromptFromQueue(string promptId)
    {
        if (_promptQueue.RemoveAll(prompt => prompt.Id == promptId) == 0) return;
        RequestStateSave();
        RenderPromptQueue();
    }

    private void ClearPromptQueue()
    {
        if (_promptQueue.Count == 0) return;
        var result = CodexMessageDialog.Show(this,
            $"Убрать из очереди все {_promptQueue.Count} сообщений?",
            "Очередь сообщений",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        _promptQueue.Clear();
        RequestStateSave();
        RenderPromptQueue();
    }

    private async Task SendPromptAsync()
    {
        if (_isSavingPrompt) return;
        if (_isClosing || Control<TextBox>("PromptInput") is not { } input) return;
        var promptText = input.Text == PromptPlaceholder ? "" : input.Text.Trim();
        var attachments = _composerAttachments.ToArray();
        var prompt = FormatPromptWithAttachments(promptText, attachments);
        if (string.IsNullOrWhiteSpace(prompt) || prompt == PromptPlaceholder) return;
        if (attachments.Length == 0 && (prompt.Equals("/goal", StringComparison.OrdinalIgnoreCase)
            || prompt.StartsWith("/goal ", StringComparison.OrdinalIgnoreCase)))
        {
            input.Clear();
            await HandleThreadGoalCommandAsync(prompt.Length > 5 ? prompt[5..].Trim() : "");
            return;
        }
        if (attachments.Length == 0 && (prompt is "/plugins" or "/skills"))
        {
            input.Clear();
            ShowExtensions(showSkills: prompt == "/skills");
            return;
        }
        if (_currentThread is null && _selectedProject is not null)
            _projectPath = _selectedProject.Path;
        if (!Directory.Exists(_projectPath))
        {
            CodexMessageDialog.Show(this, "Выберите существующую папку проекта.", "Codex", MessageBoxButton.OK, MessageBoxImage.Information);
            ChooseProjectFolder();
            return;
        }

        _currentThread ??= NewThread();
        var thread = _currentThread;
        if (thread.ActiveRun is { AppServerMode: true, SteerFollowUps: true, StopRequested: false } activeRun)
        {
            try
            {
                await WriteAppServerControlAsync(activeRun, new CodexAppServerWorkerControl { Action = "steer", Prompt = prompt });
            }
            catch (Exception error)
            {
                SetStatus("Не удалось направить сообщение в текущий ход", error.Message, isError: true);
                return;
            }
            input.Clear();
            ClearComposerAttachments();
            return;
        }
        if (thread.Messages.Count == 0 && thread.ActiveRun is null && !_promptQueue.Any(item => item.ThreadId == thread.Id))
        {
            var title = string.IsNullOrWhiteSpace(promptText)
                ? string.Join(", ", attachments.Select(Path.GetFileName))
                : promptText;
            thread.Title = TrimTitle(title);
            RenderThreads();
        }
        var project = _projects.FirstOrDefault(item => item.Id == thread.ProjectId);
        var queuedPrompt = new QueuedPrompt
        {
            ThreadId = thread.Id,
            Text = prompt,
            Model = _selectedModel,
            Effort = _selectedEffort,
            FullAccess = project?.FullAccess == true
        };
        _promptQueue.Add(queuedPrompt);
        var submittedText = input.Text;
        _isSavingPrompt = true;
        bool saved;
        try { saved = await SaveStateAsync(); }
        finally { _isSavingPrompt = false; }
        // Preserve anything typed or attached while the snapshot was being written.
        if (saved && input.Text == submittedText && _composerAttachments.SequenceEqual(attachments))
        {
            input.Clear();
            ClearComposerAttachments();
        }
        if (!saved)
        {
            _promptQueue.Remove(queuedPrompt);
            RequestStateSave();
            RenderPromptQueue();
            return;
        }
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
        var assistant = new ChatMessage(false, "Codex продолжает выполнять запрос…");
        thread.Messages.Add(assistant);
        return assistant;
    }

    private async Task ProcessQueuedPromptsAsync()
    {
        if (_isPumpingPromptQueue || _isClosing || _isEditingQueuedPrompt) return;
        _isPumpingPromptQueue = true;
        try
        {
            while (!_isClosing)
            {
                var missing = _promptQueue.RemoveAll(prompt => _threads.All(thread => thread.Id != prompt.ThreadId));
                if (missing > 0) RequestStateSave();
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
                    if (_isEditingQueuedPrompt) break;
                    if (thread.IsArchived) continue;
                }
                try
                {
                    await EnsureThreadWorktreeAsync(thread);
                }
                catch (Exception error)
                {
                    if (_currentThread == thread)
                        SetStatus("Не удалось подготовить рабочую копию", error.Message, isError: true);
                    RenderPromptQueue();
                    break;
                }
                if (_isEditingQueuedPrompt) break;
                var activeRun = CreateActiveCliRun(thread, queued);
                thread.ActiveRun = activeRun;
                var queueIndex = _promptQueue.IndexOf(queued);
                _promptQueue.Remove(queued);
                if (!await SaveStateAsync())
                {
                    thread.ActiveRun = null;
                    _promptQueue.Insert(Math.Min(queueIndex, _promptQueue.Count), queued);
                    RenderPromptQueue();
                    break;
                }
                if (_isClosing) break;
                RenderPromptQueue();
                RenderThreads();
                UpdateSendButtonState();
                if (thread.CodexSessionId is not null && thread.Messages.Count == 0)
                {
                    await RestoreThreadHistoryAsync(thread);
                    if (!thread.HistoryLoaded)
                    {
                        thread.ActiveRun = null;
                        _promptQueue.Add(queued);
                        RequestStateSave();
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
                RequestStateSave();
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
        var failed = false;
        try
        {
            var response = await RunCodexAsync(thread, activeRun, assistantMessage);
            assistantMessage.Text = string.IsNullOrWhiteSpace(response)
                ? activeRun.CompletionStatus == "interrupted"
                    ? "Ход остановлен."
                    : "Codex завершил запрос без текстового ответа. Проверьте детали команды в терминале."
                : response;
            if (assistantMessage.HasCompletedMessages && string.IsNullOrWhiteSpace(response) && activeRun.CompletionStatus != "interrupted")
                thread.Messages.Remove(assistantMessage);
            if (_currentThread == thread) SetStatus("Готов", "Ответ получен.");
        }
        catch (Exception error)
        {
            failed = true;
            assistantMessage.Text = "Не удалось выполнить запрос:\n" + error.Message;
            if (_currentThread == thread) SetStatus("Ошибка запроса", error.Message, isError: true);
        }
        finally
        {
            if (_activeProcesses.Remove(activeRun.RunId, out var activeProcess)) activeProcess?.Dispose();
            if (thread.ActiveRun?.RunId == activeRun.RunId) thread.ActiveRun = null;
            if (_currentThread != thread || !IsActive || WindowState == WindowState.Minimized || !IsVisible)
            {
                thread.HasUnreadResponse = true;
                ShowChatCompletionNotification(thread, failed);
            }
            var completionSaved = await SaveStateAsync();
            if (reloadHistoryWhenFinished && thread.CodexSessionId is not null)
                await RestoreThreadHistoryAsync(thread, force: true);
            if (completionSaved) CleanupCliRun(activeRun);
            CleanupObsoleteWorkerRuntimeCaches();
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

    private ActiveCliRun CreateActiveCliRun(ChatThread thread, QueuedPrompt queued)
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
            CompletionPath = Path.Combine(runDirectory, "completed.json"),
            WorkingDirectory = thread.WorktreePath ?? thread.ProjectPath,
            AppServerMode = true,
            InitialGoal = thread.PendingGoal
        };
    }

    private async Task EnsureThreadWorktreeAsync(ChatThread thread)
    {
        if (!thread.UseWorktree) return;
        if (thread.WorktreeSessionStarted && thread.CodexSessionId is { Length: > 0 } && thread.WorktreePath is null)
            return;
        if (!Guid.TryParseExact(thread.ProjectId, "N", out var projectId)
            || !Guid.TryParseExact(thread.Id, "N", out var threadId))
            throw new InvalidDataException("Не удалось определить безопасный идентификатор для worktree.");

        var worktreePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexGUI", "worktrees", projectId.ToString("N"), threadId.ToString("N"));
        if (thread.WorktreePath is { Length: > 0 } existingPath)
        {
            if (!string.Equals(Path.GetFullPath(existingPath), Path.GetFullPath(worktreePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Сохранённый путь worktree не совпадает с управляемой папкой CodexGUI.");
            if (Directory.Exists(worktreePath)
                && await GitRepositoryInspector.IsRegisteredWorktreeAsync(thread.ProjectPath, worktreePath, CancellationToken.None))
            {
                await EnsureWorktreeSetupAsync(thread, worktreePath);
                return;
            }
            throw new DirectoryNotFoundException("Сохранённый Git worktree больше не зарегистрирован в проекте.");
        }

        var repository = await GitRepositoryInspector.ReadAsync(thread.ProjectPath, CancellationToken.None);
        if (!repository.IsRepository)
            throw new InvalidOperationException(repository.Error ?? "Папка проекта не является Git-репозиторием.");
        thread.WorktreePath = await GitRepositoryInspector.CreateDetachedWorktreeAsync(
            repository.Root, worktreePath, CancellationToken.None);
        thread.WorktreeSetupComplete = false;
        if (!await SaveStateAsync()) throw new IOException("Не удалось сохранить путь созданного worktree.");
        if (_currentThread == thread)
        {
            _projectPath = thread.WorktreePath;
            RefreshView();
        }
        await EnsureWorktreeSetupAsync(thread, worktreePath);
    }

    private async Task EnsureWorktreeSetupAsync(ChatThread thread, string worktreePath)
    {
        if (thread.WorktreeSetupComplete) return;
        var scriptRelativePath = WorktreeSetupConfiguration.ReadScriptPath(thread.ProjectPath);
        if (scriptRelativePath is null)
        {
            thread.WorktreeSetupComplete = true;
            if (!await SaveStateAsync()) throw new IOException("Не удалось сохранить состояние подготовки worktree.");
            return;
        }

        var repository = await GitRepositoryInspector.ReadAsync(thread.ProjectPath, CancellationToken.None);
        if (!repository.IsRepository)
            throw new InvalidOperationException(repository.Error ?? "Папка проекта больше не является Git-репозиторием.");
        var projectRelativePath = Path.GetRelativePath(repository.Root, Path.GetFullPath(thread.ProjectPath));
        var worktreeProjectPath = Path.GetFullPath(Path.Combine(worktreePath, projectRelativePath));
        var relativeToWorktree = Path.GetRelativePath(Path.GetFullPath(worktreePath), worktreeProjectPath);
        if (Path.IsPathRooted(relativeToWorktree) || relativeToWorktree == ".."
            || relativeToWorktree.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Папка проекта выходит за пределы нового worktree.");
        if (!File.Exists(Path.Combine(worktreeProjectPath, ".codex", "codexgui-worktree.json")))
            throw new FileNotFoundException("Файл настройки worktree отсутствует в созданной копии. Добавьте и сохраните его в Git, затем повторите отправку.");
        var scriptPath = WorktreeSetupConfiguration.ResolveScriptPath(worktreeProjectPath, scriptRelativePath);

        var result = CodexMessageDialog.Show(this,
            $"Разрешить запуск PowerShell-сценария «{scriptRelativePath}» в новом worktree? Он выполняется с правами вашей учётной записи и может менять файлы или обращаться к сети. При отказе запрос продолжится без подготовки.",
            "Подготовить worktree",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            thread.WorktreeSetupComplete = true;
            if (!await SaveStateAsync()) throw new IOException("Не удалось сохранить отказ от setup-сценария.");
            SetStatus("Подготовка worktree пропущена", "Сценарий можно запустить для новой рабочей копии из меню проекта.");
            return;
        }

        CodexWorktreeSetupWindow.Run(this, worktreeProjectPath, scriptPath);
        thread.WorktreeSetupComplete = true;
        if (!await SaveStateAsync()) throw new IOException("Сценарий завершён, но состояние worktree не удалось сохранить.");
    }

    private async Task<string> RunCodexAsync(ChatThread thread, ActiveCliRun activeRun, ChatMessage assistantMessage)
    {
        var process = await StartOrAttachCliWorkerAsync(thread, activeRun);
        _activeProcesses[activeRun.RunId] = process;
        var response = new StringBuilder();
        var responseLock = new object();
        var completedItems = new Dictionary<string, ChatMessage>();
        var updateQueued = 0;
        var conversationRefreshQueued = 0;
        TextBlock? lastRenderedTextBlock = null;
        var lastRenderedResponseLength = 0;
        var outputTail = new CliOutputTail();
        var missingCompletionChecks = 0;

        void QueueConversationRefresh()
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || _currentThread != thread
                || Interlocked.Exchange(ref conversationRefreshQueued, 1) != 0) return;
            _ = RefreshConversationAfterDelayAsync();
        }

        async Task RefreshConversationAfterDelayAsync()
        {
            try
            {
                await Task.Delay(80).ConfigureAwait(false);
                if (_isClosing || Dispatcher.HasShutdownStarted) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_isClosing && _currentThread == thread) RenderConversation();
                }, DispatcherPriority.Background);
            }
            catch (InvalidOperationException) when (_isClosing || Dispatcher.HasShutdownStarted) { }
            catch (OperationCanceledException) when (_isClosing || Dispatcher.HasShutdownStarted) { }
            finally
            {
                Interlocked.Exchange(ref conversationRefreshQueued, 0);
            }
        }

        void QueueResponseRefresh()
        {
            if (_isClosing || Dispatcher.HasShutdownStarted) return;
            if (Interlocked.Exchange(ref updateQueued, 1) != 0) return;
            _ = RefreshResponseAfterDelayAsync();
        }

        async Task RefreshResponseAfterDelayAsync()
        {
            try
            {
                await Task.Delay(80).ConfigureAwait(false);
                if (_isClosing || Dispatcher.HasShutdownStarted) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_isClosing || thread.ActiveRun?.RunId != activeRun.RunId
                        || thread.Messages.Count == 0 || !ReferenceEquals(thread.Messages[^1], assistantMessage)) return;

                    string text;
                    lock (responseLock) text = response.ToString();
                    assistantMessage.Text = string.IsNullOrEmpty(text) ? "Думаю…" : text;
                    if (_currentThread != thread || _streamingAssistantTextBlock is not { } messageText) return;

                    var scroll = Control<ScrollViewer>("ConversationScroll");
                    var followTail = scroll is not null && IsNearConversationBottom(scroll);
                    if (!ReferenceEquals(lastRenderedTextBlock, messageText))
                    {
                        messageText.Inlines.Clear();
                        lastRenderedTextBlock = messageText;
                        lastRenderedResponseLength = 0;
                    }
                    if (text.Length < lastRenderedResponseLength)
                    {
                        messageText.Inlines.Clear();
                        lastRenderedResponseLength = 0;
                    }
                    var nextText = text.Length == 0 ? "Думаю…" : text;
                    if (nextText.Length > lastRenderedResponseLength)
                    {
                        messageText.Inlines.Add(new Run(nextText[lastRenderedResponseLength..]));
                        lastRenderedResponseLength = nextText.Length;
                    }
                    if (followTail && scroll is not null) scroll.ScrollToEnd();
                }, DispatcherPriority.Background);
            }
            catch (InvalidOperationException) when (_isClosing || Dispatcher.HasShutdownStarted) { }
            catch (OperationCanceledException) when (_isClosing || Dispatcher.HasShutdownStarted) { }
            finally
            {
                Interlocked.Exchange(ref updateQueued, 0);
            }
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
                    Dispatcher.Invoke(() =>
                    {
                        thread.CodexSessionId = id;
                        thread.ForkFromSessionId = null;
                        if (thread.UseWorktree) thread.WorktreeSessionStarted = true;
                        RequestStateSave();
                        if (_currentThread == thread) UpdateChatInfoPanel();
                    });
                }

                if (eventType == "thread.goal.applied")
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (thread.ActiveRun?.RunId != activeRun.RunId || activeRun.InitialGoal is not { } appliedGoal)
                            return;
                        var appliedThreadId = thread.CodexSessionId;
                        if (string.IsNullOrWhiteSpace(appliedThreadId)) return;
                        thread.PendingGoal = null;
                        activeRun.InitialGoal = null;
                        _threadGoalCache[appliedThreadId] = new CodexThreadGoal(
                            appliedGoal.Objective, appliedGoal.Status, appliedGoal.TokenBudget, 0, 0);
                        _threadGoalLoadFailures.Remove(appliedThreadId);
                        RequestStateSave();
                        if (_currentThread == thread) UpdateChatInfoPanel();
                    });
                    return;
                }

                if ((eventType is "approval.request" or "interactive.request") && GetString(root, "request_id") is { } requestKey &&
                    root.TryGetProperty("method", out var requestMethod) && root.TryGetProperty("params", out var requestParameters) &&
                    !activeRun.ResolvedApprovalRequestIds.Contains(requestKey, StringComparer.Ordinal))
                {
                    var method = requestMethod.GetString() ?? "";
                    var parameterSnapshot = requestParameters.Clone();
                    if (eventType == "approval.request")
                        _ = Dispatcher.BeginInvoke(new Action(async () =>
                            await ResolveAppServerApprovalAsync(thread, activeRun, requestKey, method, parameterSnapshot)));
                    else
                        _ = Dispatcher.BeginInvoke(new Action(async () =>
                            await ResolveAppServerInteractiveRequestAsync(thread, activeRun, requestKey, method, parameterSnapshot)));
                    return;
                }

                if (eventType == "server.request.resolved" && GetString(root, "request_id") is { } resolvedRequestId)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!activeRun.ResolvedApprovalRequestIds.Contains(resolvedRequestId, StringComparer.Ordinal))
                            activeRun.ResolvedApprovalRequestIds.Add(resolvedRequestId);
                        CodexAppServerRequestDialog.ClosePending(activeRun.RunId + ":" + resolvedRequestId);
                        RequestStateSave();
                    }, DispatcherPriority.Background);
                    return;
                }

                if (eventType == "turn.steer.accepted" && GetString(root, "prompt") is { Length: > 0 } steeredPrompt)
                {
                    Dispatcher.Invoke(() =>
                    {
                        var index = thread.Messages.FindLastIndex(message => !message.IsUser && message.ActivityTitle is null);
                        thread.Messages.Insert(index >= 0 ? index : thread.Messages.Count, new ChatMessage(true, steeredPrompt));
                        RequestStateSave();
                        if (_currentThread == thread) RenderConversation();
                    }, DispatcherPriority.Background);
                }

                if (eventType is "server.request.unsupported" or "control.error")
                {
                    var controlAction = GetString(root, "action");
                    var failedPrompt = GetString(root, "prompt");
                    var detail = eventType == "server.request.unsupported"
                        ? $"Codex запросил действие, которое пока не поддерживается: {GetString(root, "method") ?? "неизвестный метод"}"
                        : GetString(root, "message") ?? "Не удалось отправить действие в Codex.";
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (eventType == "control.error" && controlAction == "interrupt")
                        {
                            activeRun.StopRequested = false;
                            UpdateSendButtonState();
                        }
                        if (eventType == "control.error" && controlAction == "steer" && !string.IsNullOrWhiteSpace(failedPrompt))
                        {
                            if (_currentThread == thread && Control<TextBox>("PromptInput") is { } promptInput &&
                                (string.IsNullOrWhiteSpace(promptInput.Text) || promptInput.Text == PromptPlaceholder))
                            {
                                promptInput.Text = failedPrompt;
                                promptInput.CaretIndex = promptInput.Text.Length;
                            }
                            else if (!_promptQueue.Any(queued => queued.ThreadId == thread.Id && queued.Text == failedPrompt))
                            {
                                _promptQueue.Add(new QueuedPrompt
                                {
                                    ThreadId = thread.Id,
                                    Text = failedPrompt,
                                    Model = activeRun.Model,
                                    Effort = activeRun.Effort,
                                    FullAccess = activeRun.FullAccess
                                });
                                RenderPromptQueue();
                            }
                            RequestStateSave();
                        }
                        var index = thread.Messages.IndexOf(assistantMessage);
                        if (index >= 0)
                            thread.Messages.Insert(index, new ChatMessage(false, detail)
                            {
                                ActivityTitle = "✕  Действие Codex не выполнено",
                                IsError = true
                            });
                        if (_currentThread == thread) RenderConversation();
                        RequestStateSave();
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
                    var isAgent = GetString(item, "type") is "agent_message" or "agentMessage";
                    var message = isAgent
                        ? new ChatMessage(false, GetString(item, "text") ?? "")
                        : CreateActivityMessage(item, completed);
                    if (message is null || (isAgent && !completed)) return;
                    var itemId = GetString(item, "id");
                    ApplyItemUpdate(message, itemId, isAgent, eventType is "item.started" or "item.completed");
                }
                else if (eventType == "response.output_text.delta" && GetString(root, "delta") is { } delta)
                    AppendResponse(delta);
            }
            catch (JsonException)
            {
                AppendResponse(line + Environment.NewLine);
            }
        }

        void ApplyItemUpdate(ChatMessage message, string? itemId, bool isAgent, bool refreshActivity)
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
                if (refreshActivity && _activityWindow is { IsVisible: true }) UpdateActivityView();
                QueueConversationRefresh();
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
        activeRun.CompletionStatus = completion?.Status;
        if (completion?.SessionId is { Length: > 0 } sessionId && thread.CodexSessionId != sessionId)
        {
            thread.CodexSessionId = sessionId;
            RequestStateSave();
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

    private async Task ResolveAppServerApprovalAsync(ChatThread thread, ActiveCliRun run, string requestId,
        string method, JsonElement parameters)
    {
        if (_isClosing || thread.ActiveRun?.RunId != run.RunId || run.ResolvedApprovalRequestIds.Contains(requestId, StringComparer.Ordinal))
            return;

        ShowFromTray();
        string decision;
        SetThreadWaitingForInput(thread, waiting: true);
        try { decision = ShowAppServerApprovalDialog(method, parameters); }
        finally { SetThreadWaitingForInput(thread, waiting: false); }
        if (thread.ActiveRun?.RunId != run.RunId || _isClosing) return;
        try
        {
            await WriteAppServerControlAsync(run, new CodexAppServerWorkerControl
            {
                Action = "approval",
                RequestId = requestId,
                Decision = decision
            });
            if (!run.ResolvedApprovalRequestIds.Contains(requestId, StringComparer.Ordinal))
                run.ResolvedApprovalRequestIds.Add(requestId);
            RequestStateSave();
        }
        catch (Exception error)
        {
            SetStatus("Не удалось отправить решение Codex", error.Message, isError: true);
            var index = thread.Messages.FindLastIndex(message => !message.IsUser && message.ActivityTitle is null);
            if (index >= 0)
                thread.Messages.Insert(index, new ChatMessage(false, error.Message)
                {
                    ActivityTitle = "✕  Ошибка подтверждения",
                    IsError = true
                });
            if (_currentThread == thread) RenderConversation();
            RequestStateSave();
        }
    }

    private async Task ResolveAppServerInteractiveRequestAsync(ChatThread thread, ActiveCliRun run, string requestId,
        string method, JsonElement parameters)
    {
        if (_isClosing || thread.ActiveRun?.RunId != run.RunId || run.ResolvedApprovalRequestIds.Contains(requestId, StringComparer.Ordinal))
            return;

        ShowFromTray();
        string responseJson;
        SetThreadWaitingForInput(thread, waiting: true);
        try
        {
            responseJson = CodexAppServerRequestDialog.Show(this, run.RunId + ":" + requestId, method, parameters);
        }
        catch (Exception error)
        {
            SetStatus("Не удалось показать запрос Codex", error.Message, isError: true);
            responseJson = CodexAppServerRequestDialog.BuildSafeCancellationResponse(method, parameters);
        }
        finally { SetThreadWaitingForInput(thread, waiting: false); }
        if (thread.ActiveRun?.RunId != run.RunId || _isClosing
            || run.ResolvedApprovalRequestIds.Contains(requestId, StringComparer.Ordinal)) return;
        try
        {
            await WriteAppServerControlAsync(run, new CodexAppServerWorkerControl
            {
                Action = "response",
                RequestId = requestId,
                ResponseJson = responseJson
            });
            if (!run.ResolvedApprovalRequestIds.Contains(requestId, StringComparer.Ordinal))
                run.ResolvedApprovalRequestIds.Add(requestId);
            RequestStateSave();
        }
        catch (Exception error)
        {
            SetStatus("Не удалось отправить ответ Codex", error.Message, isError: true);
            var index = thread.Messages.FindLastIndex(message => !message.IsUser && message.ActivityTitle is null);
            if (index >= 0)
                thread.Messages.Insert(index, new ChatMessage(false, error.Message)
                {
                    ActivityTitle = "✕  Ошибка ответа Codex",
                    IsError = true
                });
            if (_currentThread == thread) RenderConversation();
            RequestStateSave();
        }
    }

    private string ShowAppServerApprovalDialog(string method, JsonElement parameters)
    {
        var isFileChange = method == "item/fileChange/requestApproval";
        var networkContext = parameters.TryGetProperty("networkApprovalContext", out var networkApproval);
        var title = isFileChange
            ? "Разрешить изменение файлов?"
            : networkContext ? "Разрешить сетевой доступ?" : "Разрешить выполнение команды?";
        var window = new Window
        {
            Owner = this,
            Title = "Подтверждение Codex",
            Width = 560,
            MaxHeight = 620,
            MinHeight = 210,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brush("#1D1F23"),
            Foreground = Brush("#F1F3F5")
        };
        CodexWindowTheme.Apply(window);
        var layout = new StackPanel { Margin = new Thickness(20) };
        layout.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var reason = GetString(parameters, "reason");
        if (!string.IsNullOrWhiteSpace(reason))
            layout.Children.Add(new TextBlock
            {
                Text = reason,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("#C8D4E2"),
                Margin = new Thickness(0, 0, 0, 10)
            });

        var detail = networkContext
            ? string.Join(" · ", new[] { GetString(networkApproval, "host"), GetString(networkApproval, "protocol") }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            : isFileChange
                ? GetString(parameters, "grantRoot")
                : GetString(parameters, "command");
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var detailText = new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = Brush("#E2E8F0")
            };
            layout.Children.Add(new Border
            {
                Background = Brush("#282B30"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = new ScrollViewer
                {
                    Content = detailText,
                    MaxHeight = 180,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            });
        }

        var location = networkContext ? null : GetString(parameters, "cwd");
        if (!string.IsNullOrWhiteSpace(location))
            layout.Children.Add(new TextBlock
            {
                Text = location,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10,
                Foreground = Brush("#9AA8B8"),
                Margin = new Thickness(0, 0, 0, 12)
            });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var hasAvailableDecisions = parameters.TryGetProperty("availableDecisions", out var available) && available.ValueKind == JsonValueKind.Array;
        var canAllow = !hasAvailableDecisions || available.EnumerateArray()
            .Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "accept");
        var canAllowForSession = hasAvailableDecisions && available.EnumerateArray()
            .Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "acceptForSession");
        void AddButton(string label, string value, string background)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 104,
                Height = 34,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(7, 0, 0, 0),
                Background = Brush(background),
                Foreground = Brush("#F1F3F5")
            };
            button.Click += (_, _) => { window.Tag = value; window.Close(); };
            buttons.Children.Add(button);
        }
        AddButton("Отклонить", "decline", "#4A2A31");
        if (canAllowForSession) AddButton("Для чата", "acceptForSession", "#344332");
        if (canAllow) AddButton("Разрешить", "accept", "#2B5A4A");
        layout.Children.Add(buttons);
        window.Content = layout;
        window.Closed += (_, _) => window.Tag ??= "cancel";
        window.ShowDialog();
        return window.Tag as string ?? "cancel";
    }

    private ProcessStartInfo CreateAppServerWorkerStartInfo(ActiveCliRun run)
    {
        var appPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить исполняемый файл CodexGUI.");
        var assemblyName = typeof(App).Assembly.GetName().Name;
        var assemblyPath = assemblyName is null
            ? ""
            : Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        var isFrameworkDependent = File.Exists(assemblyPath)
            && !File.Exists(Path.Combine(AppContext.BaseDirectory, "hostfxr.dll"));
        var startInfo = isFrameworkDependent
            ? new ProcessStartInfo(string.Equals(Path.GetFileNameWithoutExtension(appPath), "dotnet", StringComparison.OrdinalIgnoreCase)
                ? appPath
                : "dotnet.exe")
            : new ProcessStartInfo(PrepareWorkerRuntime(run, appPath));
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = Path.GetDirectoryName(run.RequestPath)!;
        if (isFrameworkDependent) startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("--app-server-worker");
        startInfo.ArgumentList.Add(run.RequestPath);
        return startInfo;
    }

    private string PrepareWorkerRuntime(ActiveCliRun run, string appPath)
    {
        var runtimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "worker-runtimes");
        var runtimeDirectory = Path.Combine(runtimeRoot, AppUpdater.CurrentVersion);
        var workerExecutable = Path.Combine(runtimeDirectory, Path.GetFileName(appPath));
        if (Directory.Exists(runtimeDirectory) && !File.Exists(workerExecutable))
        {
            var runtimeIsInUse = _threads.Any(thread => string.Equals(
                thread.ActiveRun?.WorkerRuntimeDirectory, runtimeDirectory, StringComparison.OrdinalIgnoreCase))
                || _automationRuns.Any(run => run.Status == "running"
                    && string.Equals(run.WorkerRuntimeDirectory, runtimeDirectory, StringComparison.OrdinalIgnoreCase));
            if (runtimeIsInUse)
                throw new InvalidDataException("Фоновый CodexGUI runtime неполон, но используется активным чатом.");
            Directory.Delete(runtimeDirectory, recursive: true);
        }
        if (!File.Exists(workerExecutable))
        {
            Directory.CreateDirectory(runtimeRoot);
            var stagingDirectory = runtimeDirectory + ".stage-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                foreach (var sourcePath in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(AppContext.BaseDirectory, sourcePath);
                    var destinationPath = Path.Combine(stagingDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    File.Copy(sourcePath, destinationPath);
                }
                if (!File.Exists(Path.Combine(stagingDirectory, Path.GetFileName(appPath))))
                    throw new FileNotFoundException("Не удалось подготовить отдельный CodexGUI runtime для фоновой задачи.");
                try { Directory.Move(stagingDirectory, runtimeDirectory); }
                catch (IOException) when (Directory.Exists(runtimeDirectory)) { Directory.Delete(stagingDirectory, recursive: true); }
            }
            catch
            {
                if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
                throw;
            }
        }

        run.WorkerRuntimeDirectory = runtimeDirectory;
        return workerExecutable;
    }

    private void CleanupObsoleteWorkerRuntimeCaches()
    {
        var runtimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "worker-runtimes");
        if (Directory.Exists(runtimeRoot))
        {
            var root = Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var retainedDirectories = _threads
                .Select(thread => thread.ActiveRun?.WorkerRuntimeDirectory)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in _automationRuns
                .Where(run => run.Status == "running" && !string.IsNullOrWhiteSpace(run.WorkerRuntimeDirectory))
                .Select(run => Path.GetFullPath(run.WorkerRuntimeDirectory!)))
                retainedDirectories.Add(directory);
            var currentDirectory = Path.GetFullPath(Path.Combine(runtimeRoot, AppUpdater.CurrentVersion));
            foreach (var directory in Directory.EnumerateDirectories(runtimeRoot))
            {
                var fullPath = Path.GetFullPath(directory);
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fullPath, currentDirectory, StringComparison.OrdinalIgnoreCase)
                    || retainedDirectories.Contains(fullPath)) continue;
                try { Directory.Delete(fullPath, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        CleanupObsoleteAppServerRuntimeCaches();
    }

    private void CleanupObsoleteAppServerRuntimeCaches()
    {
        var runtimeRoot = Path.GetFullPath(CodexAppServerRuntime.RuntimeRoot);
        if (!Directory.Exists(runtimeRoot)) return;
        var root = runtimeRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var retainedDirectories = _threads
            .Select(thread => thread.ActiveRun?.AppServerRuntimeDirectory)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in _automationRuns
            .Where(run => run.Status == "running" && !string.IsNullOrWhiteSpace(run.AppServerRuntimeDirectory))
            .Select(run => Path.GetFullPath(run.AppServerRuntimeDirectory!)))
            retainedDirectories.Add(directory);
        var currentDirectory = Path.GetFullPath(CodexAppServerRuntime.RuntimeDirectory);
        foreach (var directory in Directory.EnumerateDirectories(runtimeRoot))
        {
            var fullPath = Path.GetFullPath(directory);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, currentDirectory, StringComparison.OrdinalIgnoreCase)
                || retainedDirectories.Contains(fullPath)
                || (new DirectoryInfo(fullPath).Attributes & FileAttributes.ReparsePoint) != 0) continue;
            try { Directory.Delete(fullPath, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
        if (run.AppServerMode)
        {
            var appServerExecutable = CodexAppServerRuntime.ResolveExecutable();
            run.AppServerRuntimeDirectory = CodexAppServerRuntime.IsInstalled
                ? CodexAppServerRuntime.RuntimeDirectory
                : null;
            var serverRequest = new CodexAppServerWorkerRequest
            {
                RunId = run.RunId,
                ExecutablePath = appServerExecutable,
                WorkingDirectory = run.WorkingDirectory,
                Prompt = run.Prompt,
                Model = run.Model,
                Effort = run.Effort,
                FullAccess = run.FullAccess,
                ThreadId = thread.ForkFromSessionId is null ? thread.CodexSessionId : null,
                ForkFromThreadId = thread.ForkFromSessionId,
                InitialGoal = run.InitialGoal,
                StartedPath = run.StartedPath,
                OutputPath = run.OutputPath,
                ErrorPath = run.ErrorPath,
                CompletionPath = run.CompletionPath
            };
            await File.WriteAllTextAsync(run.RequestPath, JsonSerializer.Serialize(serverRequest));
            var serverStartInfo = CreateAppServerWorkerStartInfo(run);
            if (!await SaveStateAsync()) throw new IOException("Не удалось сохранить состояние перед запуском worker.");
            if (_isClosing) return null;
            var serverProcess = Process.Start(serverStartInfo)
                ?? throw new InvalidOperationException("Не удалось запустить фоновый app-server процесс.");
            run.WorkerProcessId = serverProcess.Id;
            run.WorkerStartTimeUtcTicks = serverProcess.StartTime.ToUniversalTime().Ticks;
            RequestStateSave();
            return serverProcess;
        }

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
        RequestStateSave();
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
        if (thread.ForkFromSessionId is { Length: > 0 } forkFromSessionId)
        {
            arguments.Add("fork");
            arguments.Add("--worktree");
            arguments.Add("--json");
            arguments.Add(forkFromSessionId);
            arguments.Add(run.Prompt);
        }
        else if (thread.CodexSessionId is not null)
        {
            arguments.Add("resume");
            arguments.Add("--json");
            arguments.Add(thread.CodexSessionId);
            arguments.Add(run.Prompt);
        }
        else
        {
            if (thread.UseWorktree) arguments.Add("--worktree");
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
            var pendingBytes = CollectionsMarshal.AsSpan(tail.PendingBytes);
            var lineStart = 0;
            for (var index = 0; index < pendingBytes.Length; index++)
            {
                if (pendingBytes[index] != (byte)'\n') continue;
                var length = index - lineStart;
                if (length > 0 && pendingBytes[index - 1] == (byte)'\r') length--;
                lines.Add(Encoding.UTF8.GetString(pendingBytes.Slice(lineStart, length)));
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
        _projectPath = project.Path;
        var thread = new ChatThread { Title = "Новый чат", ProjectId = project.Id, ProjectPath = project.Path };
        _threads.Add(thread);
        _currentThread = thread;
        RequestStateSave();
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
        UpdateChatInfoPanel();
        Control<TextBox>("PromptInput")?.Focus();
    }

    private void SelectThread(ChatThread thread)
    {
        if (!ReferenceEquals(_currentThread, thread)
            && thread.CodexSessionId is { Length: > 0 } sessionId
            && !_threadGoalLoadsInProgress.Contains(sessionId))
        {
            _threadGoalCache.Remove(sessionId);
            _threadGoalLoadFailures.Remove(sessionId);
        }
        _currentThread = thread;
        thread.HasUnreadResponse = false;
        _selectedProject = _projects.FirstOrDefault(project => project.Id == thread.ProjectId);
        _projectPath = thread.WorktreePath ?? _selectedProject?.Path ?? thread.ProjectPath;
        if (Control<TextBlock>("HeaderTitle") is { } title) title.Text = thread.Title;
        RefreshView();
        RequestStateSave();
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
        if (_currentThread is { } currentThread) currentThread.HasUnreadResponse = false;
        if (Control<TextBlock>("HeaderTitle") is { } title) title.Text = _currentThread?.Title ?? "Новая задача";
        RefreshView();
        RequestStateSave();
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
            CodexMessageDialog.Show(this, "В списке должен остаться хотя бы один проект.", "Проекты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = CodexMessageDialog.Show(this,
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
            RequestStateSave();
        }
    }

    private async Task ConfigureWorktreeSetupAsync(ProjectEntry project)
    {
        try
        {
            var repository = await GitRepositoryInspector.ReadAsync(project.Path, CancellationToken.None);
            if (!repository.IsRepository)
            {
                CodexMessageDialog.Show(this, repository.Error ?? "Сценарии подготовки worktree доступны только в Git-проекте.",
                    "Настройка worktree", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var currentScript = WorktreeSetupConfiguration.ReadScriptPath(project.Path);
            var scriptPath = CodexFilePickerWindow.OpenFile(this,
                "Выберите setup-сценарий для worktree", project.Path, [".ps1"], currentScript);
            if (scriptPath is null) return;

            var relativePath = WorktreeSetupConfiguration.SaveScriptPath(project.Path, scriptPath);
            CodexMessageDialog.Show(this,
                $"Сценарий «{relativePath}» записан в .codex/codexgui-worktree.json. Добавьте в Git файл настройки и сам сценарий. Перед запуском в каждом новом worktree CodexGUI запросит подтверждение.",
                "Подготовка worktree настроена", MessageBoxButton.OK, MessageBoxImage.Information);
            RenderProjects();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            CodexMessageDialog.Show(this, error.Message, "Не удалось настроить подготовку worktree",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveWorktreeSetup(ProjectEntry project)
    {
        if (CodexMessageDialog.Show(this,
            $"Удалить файл настройки .codex/codexgui-worktree.json из проекта «{project.Name}»? Сам setup-сценарий останется на месте.",
            "Убрать сценарий подготовки", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            WorktreeSetupConfiguration.Remove(project.Path);
            RenderProjects();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CodexMessageDialog.Show(this, error.Message, "Не удалось удалить настройку worktree",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdatePanelLayout()
    {
        if (_activeView is not { ColumnDefinitions.Count: 4 } view) return;
        // Keep the conversation usable when repository details are open in a narrow window.
        var autoHideSidebar = _contextOpen && view.ActualWidth > 0 && view.ActualWidth < 1120 && !_sidebarAutoHideOverridden;
        var hideSidebar = _sidebarCollapsed || autoHideSidebar;
        view.ColumnDefinitions[0].Width = new GridLength(42);
        view.ColumnDefinitions[1].Width = new GridLength(hideSidebar ? 0 : 280);
        if (Control<Border>("SidebarPanel") is { } sidebar)
            sidebar.Visibility = hideSidebar ? Visibility.Collapsed : Visibility.Visible;
        if (Control<Border>("ChatOverviewCard") is { } overview)
            overview.Width = view.ActualWidth > 0 && view.ActualWidth < 1250 ? 280 : 390;
        view.ColumnDefinitions[3].Width = new GridLength(_contextOpen ? 310 : 0);
    }

    private void ToggleSidebar()
    {
        if (_activeView is not { } view) return;
        var autoHideSidebar = _contextOpen && view.ActualWidth > 0 && view.ActualWidth < 1120 && !_sidebarAutoHideOverridden;
        var isHidden = _sidebarCollapsed || autoHideSidebar;
        _sidebarCollapsed = !isHidden;
        _sidebarAutoHideOverridden = isHidden;
        UpdatePanelLayout();
    }

    private void ToggleContext(bool? open = null)
    {
        _contextOpen = open ?? !_contextOpen;
        UpdatePanelLayout();
        if (Control<Border>("ContextPanel") is { } panel) panel.Visibility = _contextOpen ? Visibility.Visible : Visibility.Collapsed;
        if (_contextOpen)
        {
            RefreshFileTree();
            UpdateChatInfoPanel();
            _ = RefreshRepositoryInfoAsync();
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
            Foreground = Brush("#F1F3F5"),
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
                var result = CodexMessageDialog.Show(this,
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
            Background = Brush("#1D1F23"),
            BorderBrush = Brush("#34373E"),
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
            BorderBrush = selected ? Brush("#34373E") : Brushes.Transparent,
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
            ?? throw new InvalidDataException($"Выбранная модель {_selectedModel} отсутствует в каталоге Codex App Server.");
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
            _modelCatalogTask ??= new CodexAppServerClient(CodexAppServerRuntime.ResolveExecutable(), _appServerCancellation.Token)
                .ReadModelCatalogAsync(DefaultEffort);
            _availableModels = await _modelCatalogTask;
            if (_availableModels.Count == 0)
                throw new InvalidDataException("Codex App Server не сообщил доступные модели.");
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
            Foreground = Brush("#F1F3F5"),
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
            Background = Brush("#1D1F23"),
            BorderBrush = Brush("#34373E"),
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
        RequestStateSave();
    }

    private void SetSelectedEffort(string effort)
    {
        _selectedEffort = effort;
        UpdateComposerSettingsLabels();
        RequestStateSave();
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
        RequestStateSave();
    }

    private void ApplyAccessMode()
    {
        if (Control<TextBlock>("AccessModeLabel") is { } label)
        {
            label.Text = IsFullAccess ? "Полный доступ" : "Попросить подтверждение";
            label.Foreground = Brush(IsFullAccess ? "#F0A277" : "#C4C5C9");
        }
        RequestStateSave();
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
        var parentDirectory = CodexFilePickerWindow.ChooseFolder(this, "Где создать папку проекта?",
            Directory.Exists(_projectPath) ? _projectPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (parentDirectory is null) return;

        var name = AskForProjectName();
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            CodexMessageDialog.Show(this, "В названии есть недопустимые символы.", "Новый проект", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var path = Path.Combine(parentDirectory, name);
        if (Directory.Exists(path) || File.Exists(path))
        {
            CodexMessageDialog.Show(this, "Папка или файл с таким именем уже существует. Выберите другое имя.", "Новый проект", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            AddProjectPath(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CodexMessageDialog.Show(this, error.Message, "Не удалось создать проект", MessageBoxButton.OK, MessageBoxImage.Error);
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
            Background = Brush("#1D1F23"),
            Foreground = Brush("#F1F3F5")
        };
        CodexWindowTheme.Apply(dialog);
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "Название проекта", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 11) });
        var input = new TextBox { Height = 36, Padding = new Thickness(9, 7, 9, 5), Background = Brush("#25272C") };
        content.Children.Add(input);
        var create = new Button { Content = "Создать папку", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Background = Brush("#204139"), Foreground = Brush("#92E5C7"), BorderBrush = Brush("#365C53"), BorderThickness = new Thickness(1) };
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

    private void ChooseProjectFolder()
    {
        var folder = CodexFilePickerWindow.ChooseFolder(this, "Добавить существующую папку как проект",
            Directory.Exists(_projectPath) ? _projectPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (folder is null) return;
        AddProjectPath(folder);
    }

    private void OpenProjectFolder()
    {
        if (!Directory.Exists(_projectPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", _projectPath) { UseShellExecute = true });
    }

    private void AddFileContext()
    {
        var file = CodexFilePickerWindow.OpenFile(this, "Добавить файл в контекст",
            Directory.Exists(_projectPath) ? _projectPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), []);
        if (file is null || Control<TextBox>("PromptInput") is not { } input) return;
        AddComposerAttachments([file]);
        input.Focus();
    }

    private void ShowActionsWindow()
    {
        var projectPath = _selectedProject?.Path;
        var actionWorkingDirectory = _currentThread?.WorktreePath ?? projectPath ?? _projectPath;
        var actionHasFullAccess = IsFullAccess;
        var projectActionsAvailable = !string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath);
        List<CodexActionDefinition> projectActions = [];
        string? projectActionsRevision = null;
        if (projectActionsAvailable)
        {
            try
            {
                projectActions = CodexProjectActionStore.Load(projectPath!, out projectActionsRevision);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                CodexMessageDialog.Show(this,
                    $"Не удалось прочитать .codex/codexgui-actions.json выбранного проекта: {error.Message}",
                    "Не удалось прочитать действия проекта", MessageBoxButton.OK, MessageBoxImage.Error);
                projectActionsAvailable = false;
            }
        }

        var window = new CodexActionsWindow(
            _actions,
            projectActions,
            SaveState,
            () =>
            {
                if (!projectActionsAvailable) return false;
                projectActionsRevision = CodexProjectActionStore.Save(projectPath!, projectActions, projectActionsRevision);
                return true;
            },
            projectActionsAvailable,
            InsertActionPrompt,
            (name, command) => RunActionCommand(name, command, actionWorkingDirectory, actionHasFullAccess))
        { Owner = this };
        window.ShowDialog();
    }

    private void RunActionCommand(string name, string command, string? workingDirectory, bool fullAccess)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                CodexMessageDialog.Show(this, "Сначала выберите доступную папку проекта или рабочую копию.",
                    "Команда PowerShell", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var window = new CodexTerminalWindow(workingDirectory, fullAccess, command, $"Действие: {name}")
            {
                Owner = this
            };
            window.Show();
        }), DispatcherPriority.Background);
    }

    private void InsertActionPrompt(string prompt)
    {
        if (Control<TextBox>("PromptInput") is not { } input) return;
        var current = input.Text == PromptPlaceholder ? "" : input.Text;
        input.Text = string.IsNullOrWhiteSpace(current)
            ? prompt
            : current.TrimEnd() + Environment.NewLine + Environment.NewLine + prompt;
        input.Foreground = Brush("#F1F3F5");
        input.Focus();
        input.CaretIndex = input.Text.Length;
    }

    private async Task ShowAutomationsAsync()
    {
        if (_automationWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        if (_availableModels.Count == 0 && Control<Button>("ModelButton") is { } modelButton)
            await EnsureModelCatalogAsync(modelButton);
        var projects = _projects.Select(project => new CodexAutomationProject(project.Id, project.Name, project.Path)).ToArray();
        var chats = _threads.Where(thread => !thread.IsArchived && !string.IsNullOrWhiteSpace(thread.CodexSessionId))
            .Select(thread => new CodexAutomationChat(
                thread.ProjectId, thread.CodexSessionId!, thread.Title, thread.ProjectPath, thread.WorktreePath))
            .ToArray();
        _automationWindow = new CodexAutomationsWindow(
            _automationDefinitions,
            _automationRuns,
            projects,
            chats,
            _availableModels,
            _selectedProject?.Id,
            PersistAutomationDefinitions,
            definition => StartAutomationAsync(definition, manualRun: true),
            OpenAutomationRun,
            MarkAutomationRunReviewed)
        {
            Owner = this
        };
        _automationWindow.Closed += (_, _) => _automationWindow = null;
        _automationWindow.ShowDialog();
    }

    private async Task ShowSubagentsAsync()
    {
        if (_currentThread is not { } parent)
        {
            CodexMessageDialog.Show(this, "Сначала откройте чат.", "Субагенты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (parent.ActiveRun is null && parent.CodexSessionId is { Length: > 0 } && !parent.HistoryLoaded)
            await RestoreThreadHistoryAsync(parent);

        IReadOnlyList<CodexSubagentInfo> ReadCurrentSubagents() => parent.Messages
            .SelectMany(message => message.Subagents)
            .GroupBy(agent => agent.ThreadId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();

        var dialog = new CodexSubagentsWindow(parent.Title, ReadCurrentSubagents, OpenSubagentThreadAsync) { Owner = this };
        dialog.ShowDialog();
    }

    private void ShowTerminalWindow()
    {
        if (_terminalWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }
        var workingDirectory = _currentThread?.WorktreePath ?? _selectedProject?.Path ?? _projectPath;
        if (!Directory.Exists(workingDirectory))
        {
            CodexMessageDialog.Show(this, "Сначала выберите доступную папку проекта.", "Терминал", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _terminalWindow = new CodexTerminalWindow(workingDirectory, IsFullAccess) { Owner = this };
        _terminalWindow.Closed += (_, _) => _terminalWindow = null;
        _terminalWindow.Show();
    }

    private void ShowExtensions(bool showSkills)
    {
        try
        {
            var dialog = new ExtensionsWindow(_projectPath, skill =>
            {
                if (Control<TextBox>("PromptInput") is not { } input) return;
                if (input.Text == PromptPlaceholder) input.Clear();
                input.Text = $"${skill.Name} " + input.Text;
                input.CaretIndex = input.Text.Length;
            }, showSkills) { Owner = this };
            dialog.ShowDialog();
            Control<TextBox>("PromptInput")?.Focus();
        }
        catch (Exception error)
        {
            SetStatus("Не удалось открыть плагины и скиллы", error.Message, isError: true);
        }
    }

    private void ShowMcpServers()
    {
        try
        {
            var dialog = new CodexMcpServerStatusWindow(CodexAppServerRuntime.ResolveExecutable()) { Owner = this };
            dialog.ShowDialog();
            Control<TextBox>("PromptInput")?.Focus();
        }
        catch (Exception error)
        {
            SetStatus("Не удалось открыть MCP-серверы", error.Message, isError: true);
        }
    }

    private async Task CaptureScreenshotAsync()
    {
        using var selector = new ScreenshotSelectionForm();
        if (selector.ShowDialog() != Forms.DialogResult.OK || selector.SelectionBounds.Width < 2 || selector.SelectionBounds.Height < 2)
            return;

        var savePath = CodexFilePickerWindow.SaveFile(this, "Сохранить скриншот",
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            $"CodexGUI-{DateTime.Now:yyyyMMdd-HHmmss}.png", ".png");
        if (savePath is null) return;

        try
        {
            await Task.Delay(120);
            var bounds = selector.SelectionBounds;
            using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, System.Drawing.CopyPixelOperation.SourceCopy);
            bitmap.Save(savePath, System.Drawing.Imaging.ImageFormat.Png);

            if (Control<TextBox>("PromptInput") is { } input)
            {
                AddComposerAttachments([savePath]);
                input.Focus();
            }
            SetStatus("Скриншот готов", $"Сохранен и добавлен в контекст: {Path.GetFileName(savePath)}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            CodexMessageDialog.Show(this, error.Message, "Не удалось сохранить скриншот", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryPasteContext(TextBox input)
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null) return false;

            var paths = new List<string>();
            if (data.GetData(DataFormats.FileDrop) is string[] files)
            {
                paths.AddRange(files.Where(File.Exists));
            }
            else if (data.GetData(DataFormats.Bitmap) is BitmapSource image)
            {
                var directory = Path.Combine(Path.GetDirectoryName(GetStatePath())!, "attachments");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"clipboard-{Guid.NewGuid():N}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    encoder.Save(stream);
                paths.Add(path);
            }

            if (paths.Count == 0) return false;
            AddComposerAttachments(paths);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ExternalException or NotSupportedException)
        {
            SetStatus("Не удалось вставить вложение", error.Message, isError: true);
            return true;
        }
    }

    private void ShowSearch()
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "Поиск по чатам",
            Width = 520,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("#1D1F23"),
            Foreground = Brush("#F1F3F5")
        };
        CodexWindowTheme.Apply(dialog);
        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(new TextBlock { Text = "Найти чат", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        var query = new TextBox { Height = 38, Padding = new Thickness(10, 8, 10, 5), Background = Brush("#25272C"), Text = "" };
        var searchCurrentMessages = new CheckBox
        {
            Content = "Искать текст в сообщениях открытого чата",
            IsEnabled = _currentThread is { HistoryLoading: false } current
                && current.Messages.Any(message => message.ActivityTitle is null && !string.IsNullOrWhiteSpace(message.Text)),
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "Ищет в уже загруженных сообщениях текущего чата"
        };
        var status = new TextBlock
        {
            Text = "Введите часть названия чата или текста сообщения.",
            Foreground = Brush("#B0B5BE"),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 8)
        };
        var matches = new ListBox
        {
            Height = 270,
            Background = Brush("#151619"),
            Foreground = Brush("#F1F3F5"),
            BorderBrush = Brush("#34373E"),
            BorderThickness = new Thickness(1),
            ItemTemplate = CreateChatSearchItemTemplate()
        };
        VirtualizingPanel.SetIsVirtualizing(matches, true);
        VirtualizingPanel.SetVirtualizationMode(matches, VirtualizationMode.Recycling);
        var open = new Button
        {
            Content = "Открыть чат",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Background = Brush("#34373E"),
            IsEnabled = false,
            IsDefault = true
        };
        var searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        searchDebounce.Tick += (_, _) => UpdateMatches();

        void UpdateMatches()
        {
            searchDebounce.Stop();
            var normalizedQuery = query.Text.Trim();
            var matchingThreads = FindChatSearchMatches(_threads, normalizedQuery);
            var titleMatches = matchingThreads.Select(thread =>
            {
                var projectName = _projects.FirstOrDefault(project => project.Id == thread.ProjectId)?.Name
                    ?? Path.GetFileName(thread.ProjectPath.TrimEnd(Path.DirectorySeparatorChar));
                var details = string.IsNullOrWhiteSpace(projectName) ? thread.ProjectPath : projectName;
                if (thread.IsArchived) details += " · архив";
                if (thread.WorktreePath is not null) details += " · worktree";
                return new ChatSearchMatch(thread, thread.Title, details);
            }).ToArray();
            IReadOnlyList<ChatSearchMatch> messageMatches = searchCurrentMessages.IsChecked == true && _currentThread is { } currentThread
                ? FindCurrentMessageMatches(currentThread, normalizedQuery)
                : Array.Empty<ChatSearchMatch>();
            var results = titleMatches.Concat(messageMatches).ToArray();
            matches.ItemsSource = results;

            if (matches.Items.Count > 0) matches.SelectedIndex = 0;
            UpdateOpenButton();
            status.Text = normalizedQuery.Length == 0
                ? "Введите часть названия чата или текста сообщения."
                : results.Length == 0
                    ? "Совпадений нет."
                    : searchCurrentMessages.IsChecked == true
                        ? $"Найдено совпадений: чаты — {titleMatches.Length}, сообщения — {messageMatches.Count}."
                        : $"Найдено совпадений: {titleMatches.Length}. Последние чаты сверху.";
            matches.IsEnabled = true;
        }

        void ScheduleMatches()
        {
            searchDebounce.Stop();
            matches.IsEnabled = false;
            open.IsEnabled = false;
            status.Text = "Поиск…";
            searchDebounce.Start();
        }

        void UpdateOpenButton()
        {
            var selectedMatch = matches.SelectedItem as ChatSearchMatch;
            open.IsEnabled = selectedMatch is not null;
            open.Content = selectedMatch?.Message is null ? "Открыть чат" : "Перейти к сообщению";
        }

        query.TextChanged += (_, _) => ScheduleMatches();
        searchCurrentMessages.Checked += (_, _) => UpdateMatches();
        searchCurrentMessages.Unchecked += (_, _) => UpdateMatches();
        matches.SelectionChanged += (_, _) => UpdateOpenButton();
        matches.MouseDoubleClick += (_, _) => open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        open.Click += (_, _) =>
        {
            if (matches.SelectedItem is not ChatSearchMatch searchMatch) return;
            var thread = searchMatch.Thread;
            var message = searchMatch.Message;
            _showArchivedChats = thread.IsArchived;
            SelectThread(thread);
            dialog.Close();
            if (message is not null)
                Dispatcher.BeginInvoke(() => ScrollToSearchMessage(thread, message), DispatcherPriority.Background);
        };
        layout.Children.Add(query);
        layout.Children.Add(searchCurrentMessages);
        layout.Children.Add(status);
        layout.Children.Add(matches);
        layout.Children.Add(open);
        dialog.Content = layout;
        dialog.Loaded += (_, _) => query.Focus();
        dialog.Closed += (_, _) => searchDebounce.Stop();
        UpdateMatches();
        dialog.ShowDialog();
    }

    private static IReadOnlyList<ChatThread> FindChatSearchMatches(IEnumerable<ChatThread> threads, string query)
    {
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length == 0) return [];
        return threads
            .Where(thread => thread.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Reverse()
            .ToArray();
    }

    private static IReadOnlyList<ChatSearchMatch> FindCurrentMessageMatches(ChatThread thread, string query)
    {
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length == 0) return [];
        var matches = new List<ChatSearchMatch>();
        for (var index = thread.Messages.Count - 1; index >= 0; index--)
        {
            var message = thread.Messages[index];
            if (message.ActivityTitle is not null || string.IsNullOrWhiteSpace(message.Text)) continue;
            var matchIndex = message.Text.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0) continue;

            var details = $"{(message.IsUser ? "Ваше сообщение" : "Ответ Codex")}: {CreateMessageSearchSnippet(message.Text, matchIndex)}";
            if (thread.IsArchived) details += " · архив";
            if (thread.WorktreePath is not null) details += " · worktree";
            matches.Add(new ChatSearchMatch(thread, thread.Title, details, message));
        }
        return matches;
    }

    private static string CreateMessageSearchSnippet(string text, int matchIndex)
    {
        var start = Math.Max(0, matchIndex - 45);
        var end = Math.Min(text.Length, start + 120);
        var snippet = text[start..end].Replace('\r', ' ').Replace('\n', ' ');
        return (start > 0 ? "…" : "") + snippet + (end < text.Length ? "…" : "");
    }

    private void ScrollToSearchMessage(ChatThread thread, ChatMessage message)
    {
        if (!ReferenceEquals(_currentThread, thread)
            || Control<ScrollViewer>("ConversationScroll") is not { } scroll
            || Control<StackPanel>("Conversation") is not { } stack)
            return;
        var element = stack.Children.OfType<FrameworkElement>()
            .FirstOrDefault(child => ReferenceEquals(child.Tag, message));
        if (element is null) return;
        stack.UpdateLayout();
        var position = element.TransformToAncestor(scroll).Transform(new Point(0, 0));
        var targetOffset = Math.Clamp(scroll.VerticalOffset + position.Y - 24, 0, scroll.ScrollableHeight);
        scroll.ScrollToVerticalOffset(targetOffset);
    }

    private static DataTemplate CreateChatSearchItemTemplate()
        => (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel>
                <TextBlock Text="{Binding Title}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                <TextBlock Text="{Binding Details}" Foreground="#B0B5BE" FontSize="10" TextTrimming="CharacterEllipsis" Margin="0,3,0,0" />
              </StackPanel>
            </DataTemplate>
            """);

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
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && sender is TextBox input)
        {
            e.Handled = TryPasteContext(input);
            return;
        }
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendPromptAsync();
        }
    }

    private void CleanupWindowResources()
    {
        _inputLanguageManager.InputLanguageChanged -= PromptInputLanguageChanged;
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
        public string? WorktreePath { get; set; }
        public string? CodexSessionId { get; set; }
        public string? ForkFromSessionId { get; set; }
        public CodexAppServerGoalDraft? PendingGoal { get; set; }
        public bool IsArchived { get; set; }
        public bool IsPinned { get; set; }
        public bool HasUnreadResponse { get; set; }
        public bool WaitingForInput { get; set; }
        public bool IsForking { get; set; }
        public bool UseWorktree { get; set; }
        public bool WorktreeSessionStarted { get; set; }
        public bool WorktreeSetupComplete { get; set; }
        public long? ContextTokens { get; set; }
        public long? ContextWindowTokens { get; set; }
        public ActiveCliRun? ActiveRun { get; set; }
        public bool HistoryLoaded { get; set; }
        public bool HistoryLoading { get; set; }
        public string? HistoryError { get; set; }
        public Task? HistoryRestoreTask { get; set; }
        public List<ChatMessage> Messages { get; } = [];
    }

    private sealed record ChatSearchMatch(ChatThread Thread, string Title, string Details, ChatMessage? Message = null);

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
        public string? WorktreePath { get; set; }
        public string? CodexSessionId { get; set; }
        public string? ForkFromSessionId { get; set; }
        public CodexAppServerGoalDraft? PendingGoal { get; set; }
        public bool IsArchived { get; set; }
        public bool IsPinned { get; set; }
        public bool HasUnreadResponse { get; set; }
        public bool UseWorktree { get; set; }
        public bool WorktreeSessionStarted { get; set; }
        public bool WorktreeSetupComplete { get; set; }
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
        public string WorkingDirectory { get; set; } = "";
        public string RequestPath { get; set; } = "";
        public string ScriptPath { get; set; } = "";
        public string StartedPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string ErrorPath { get; set; } = "";
        public string CompletionPath { get; set; } = "";
        public bool AppServerMode { get; set; }
        public bool SteerFollowUps { get; set; }
        public bool StopRequested { get; set; }
        public List<string> ResolvedApprovalRequestIds { get; set; } = [];
        public string? CompletionStatus { get; set; }
        public CodexAppServerGoalDraft? InitialGoal { get; set; }
        public string? WorkerRuntimeDirectory { get; set; }
        public string? AppServerRuntimeDirectory { get; set; }
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
        public string? Status { get; set; }
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
        public List<CodexActionDefinition> Actions { get; set; } = [];
        public List<StoredThread> Threads { get; set; } = [];
    }

    private sealed record UsageSnapshot(IReadOnlyList<UsageWindow> Windows);
    private sealed record UsageWindow(long? DurationMinutes, int UsedPercent, long? ResetsAt);

    private sealed class CodexTrayColorTable : Forms.ProfessionalColorTable
    {
        public CodexTrayColorTable() => UseSystemColors = false;

        public override DrawingColor ToolStripDropDownBackground => DrawingColor.FromArgb(22, 33, 43);
        public override DrawingColor MenuBorder => DrawingColor.FromArgb(42, 57, 70);
        public override DrawingColor MenuItemBorder => DrawingColor.FromArgb(54, 92, 83);
        public override DrawingColor MenuItemSelectedGradientBegin => DrawingColor.FromArgb(32, 58, 52);
        public override DrawingColor MenuItemSelectedGradientEnd => DrawingColor.FromArgb(32, 58, 52);
        public override DrawingColor MenuItemPressedGradientBegin => DrawingColor.FromArgb(32, 58, 52);
        public override DrawingColor MenuItemPressedGradientEnd => DrawingColor.FromArgb(32, 58, 52);
        public override DrawingColor SeparatorDark => DrawingColor.FromArgb(42, 57, 70);
        public override DrawingColor SeparatorLight => DrawingColor.FromArgb(42, 57, 70);
    }

    private sealed class ChatMessage(bool isUser, string text)
    {
        public bool IsUser { get; } = isUser;
        public string Text { get; set; } = text;
        public string? ActivityTitle { get; set; }
        public string? RelatedThreadId { get; set; }
        public List<CodexSubagentInfo> Subagents { get; set; } = [];
        public bool IsError { get; set; }
        public bool IsExpanded { get; set; }
        public bool HasCompletedMessages { get; set; }
    }
}
