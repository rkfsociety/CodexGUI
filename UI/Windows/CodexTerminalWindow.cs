using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class CodexTerminalWindow : Window
{
    private readonly string _workingDirectory;
    private readonly bool _fullAccess;
    private readonly string? _initialCommand;
    private readonly CodexAppServerTerminalHost _host = new();
    private readonly Dictionary<string, TerminalTab> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closingSessions = new(StringComparer.Ordinal);
    private readonly TerminalOutputBatchBuffer _terminalOutputBuffer = new();
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = new();
    private int _terminalNumber;
    private bool _isClosing;
    private bool? _terminalAllowed;

    public CodexTerminalWindow(string workingDirectory, bool fullAccess, string? initialCommand = null, string? windowTitle = null)
    {
        _workingDirectory = workingDirectory;
        _fullAccess = fullAccess;
        _initialCommand = initialCommand;
        Title = string.IsNullOrWhiteSpace(windowTitle) ? "Терминалы Codex" : windowTitle;
        Width = 1050;
        Height = 720;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
        _host.OutputReceived += OnOutputReceived;
        _host.SessionExited += OnSessionExited;
        Loaded += async (_, _) => await CreateTerminalAsync(_initialCommand);
        Closed += (_, _) =>
        {
            _isClosing = true;
            _terminalOutputBuffer.Clear();
            _ = _host.DisposeAsync();
        };
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Margin = new Thickness(1, 0, 1, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "Терминалы", FontSize = 20, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock
        {
            Text = $"{_workingDirectory} · {(_fullAccess ? "полный доступ проекта" : "PTY запросит отдельное разрешение")}",
            Foreground = Brush("#9AA8B8"),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        });
        header.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var add = MakeButton("＋ Новый терминал", accent: true);
        add.Click += async (_, _) => await CreateTerminalAsync();
        actions.Children.Add(add);
        _status.Text = "App Server готов";
        _status.Foreground = Brush("#9AA8B8");
        _status.FontSize = 10;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(10, 0, 0, 0);
        actions.Children.Add(_status);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        root.Children.Add(header);

        _tabs.Background = Brush("#101114");
        _tabs.BorderThickness = new Thickness(0);
        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);
        return root;
    }

    private async Task CreateTerminalAsync(string? initialCommand = null)
    {
        if (_isClosing || !Directory.Exists(_workingDirectory)) return;
        if (!await EnsureTerminalConsentAsync()) return;
        var processId = Guid.NewGuid().ToString("N");
        var tab = new TerminalTab(processId, $"PowerShell {_terminalNumber + 1}");
        _terminalNumber++;
        var item = new TabItem { Tag = tab, Header = BuildTabHeader(tab), Content = BuildTerminalContent(tab) };
        tab.Item = item;
        _sessions.Add(processId, tab);
        _tabs.Items.Add(item);
        _tabs.SelectedItem = item;
        _status.Text = "Запуск App Server…";
        try
        {
            await _host.StartSessionAsync(processId, _workingDirectory, _fullAccess || _terminalAllowed == true);
            tab.IsRunning = true;
            _status.Text = "PowerShell · команда отправляется по Enter";
            if (!string.IsNullOrEmpty(initialCommand))
                await SendInputAsync(tab, initialCommand + "\r");
            tab.Input.Focus();
        }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception error)
        {
            if (_isClosing) return;
            tab.IsRunning = false;
            AppendOutput(tab, "\r\nНе удалось запустить PowerShell: " + error.Message + "\r\n");
            _status.Text = "Терминал не запущен";
        }
    }

    private Task<bool> EnsureTerminalConsentAsync()
    {
        if (_fullAccess || _terminalAllowed == true) return Task.FromResult(true);
        var answer = CodexMessageDialog.Show(this,
            "Для интерактивного PowerShell терминала App Server запускает процесс вне Windows sandbox. " +
            "Он будет работать с правами вашей учётной записи Windows и сможет читать или изменять файлы вне проекта. " +
            "Продолжать?",
            "Запустить терминал с полным доступом",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        _terminalAllowed = answer == MessageBoxResult.Yes;
        _status.Text = _terminalAllowed == true ? "Терминал работает вне sandbox" : "Запуск отменён";
        return Task.FromResult(_terminalAllowed == true);
    }

    private UIElement BuildTabHeader(TerminalTab tab)
    {
        var header = new DockPanel { LastChildFill = true };
        tab.HeaderLabel = new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(tab.HeaderLabel);
        var close = new Button
        {
            Content = "×",
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = Brush("#A4B1C2"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = "Завершить терминал"
        };
        close.Click += async (_, _) => await CloseTerminalAsync(tab.ProcessId);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Insert(0, close);
        return header;
    }

    private UIElement BuildTerminalContent(TerminalTab tab)
    {
        var layout = new Grid { Margin = new Thickness(2, 8, 2, 2) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tab.Output = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(12),
            Background = Brush("#151619"),
            Foreground = Brush("#D5DEE9"),
            BorderBrush = Brush("#34373E"),
            BorderThickness = new Thickness(1)
        };
        layout.Children.Add(tab.Output);
        tab.Input = new TextBox
        {
            MinHeight = 36,
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 8, 0, 0),
            Background = Brush("#1D1F23"),
            Foreground = Brush("#F1F3F5"),
            BorderBrush = Brush("#34373E"),
            BorderThickness = new Thickness(1),
            ToolTip = "Введите команду и нажмите Enter. Выделенный текст копируется через Ctrl+C; без выделения Ctrl+C прерывает команду."
        };
        tab.Input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                if (!tab.IsRunning)
                {
                    // A stopped or not-yet-started session must not discard the user's draft.
                    _status.Text = "Терминал не запущен; введённая команда сохранена.";
                    return;
                }
                // Capture and clear before awaiting I/O: later typing belongs to the next command.
                var command = tab.Input.Text;
                tab.Input.Clear();
                tab.HistoryDraft = "";
                if (!string.IsNullOrWhiteSpace(command))
                {
                    tab.History.Add(command);
                    // Keep long-lived terminal tabs from retaining an unlimited command history.
                    if (tab.History.Count > 200) tab.History.RemoveAt(0);
                }
                tab.HistoryIndex = tab.History.Count;
                await SendInputAsync(tab, command + "\r");
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.C)
            {
                if (tab.Input.SelectionLength == 0)
                {
                    e.Handled = true;
                    await SendInputAsync(tab, "\x03");
                }
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.D)
            {
                e.Handled = true;
                await SendInputAsync(tab, "\x04");
            }
            else if (e.Key == Key.Up && tab.HistoryIndex > 0)
            {
                e.Handled = true;
                // Preserve the unfinished command while browsing earlier entries.
                if (tab.HistoryIndex == tab.History.Count)
                    tab.HistoryDraft = tab.Input.Text;
                tab.HistoryIndex--;
                tab.Input.Text = tab.History[tab.HistoryIndex];
                tab.Input.CaretIndex = tab.Input.Text.Length;
            }
            else if (e.Key == Key.Down && tab.HistoryIndex < tab.History.Count)
            {
                e.Handled = true;
                tab.HistoryIndex++;
                tab.Input.Text = tab.HistoryIndex == tab.History.Count
                    ? tab.HistoryDraft : tab.History[tab.HistoryIndex];
                tab.Input.CaretIndex = tab.Input.Text.Length;
            }
        };
        Grid.SetRow(tab.Input, 1);
        layout.Children.Add(tab.Input);
        return layout;
    }

    private async Task SendInputAsync(TerminalTab tab, string input)
    {
        if (!tab.IsRunning) return;
        try { await _host.WriteInputAsync(tab.ProcessId, input); }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception error)
        {
            if (!_isClosing) AppendOutput(tab, "\r\n[ввод не отправлен: " + error.Message + "]\r\n");
        }
    }

    private async Task CloseTerminalAsync(string processId)
    {
        if (!_sessions.TryGetValue(processId, out var tab) || !_closingSessions.Add(processId)) return;
        tab.HeaderLabel.Text = tab.Title + " · завершение…";
        try
        {
            if (!await _host.TerminateSessionAsync(processId))
            {
                if (_isClosing) return;
                tab.HeaderLabel.Text = tab.IsRunning ? tab.Title : tab.Title + " · завершён";
                _status.Text = "Терминал не подтвердил завершение. Повторите закрытие.";
                AppendOutput(tab, "\r\n[не удалось завершить терминал; вкладка оставлена для повтора]\r\n");
                return;
            }

            if (_isClosing) return;
            _sessions.Remove(processId);
            _tabs.Items.Remove(tab.Item);
            if (_sessions.Count == 0) _status.Text = "Все терминалы закрыты";
        }
        catch (Exception error)
        {
            if (_isClosing) return;
            tab.HeaderLabel.Text = tab.IsRunning ? tab.Title : tab.Title + " · завершён";
            _status.Text = "Не удалось завершить терминал: " + error.Message;
        }
        finally { _closingSessions.Remove(processId); }
    }

    private void OnOutputReceived(CodexTerminalOutput output)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        // Keep raw chunks in order: ANSI escape sequences can span several notifications.
        // The bounded buffer also trims oversized chunks without splitting surrogate pairs.
        if (!_terminalOutputBuffer.Enqueue(output)) return;

        try { _ = Dispatcher.BeginInvoke((Action)FlushPendingTerminalOutput); }
        catch (InvalidOperationException) { _terminalOutputBuffer.Clear(); }
    }

    private void FlushPendingTerminalOutput()
    {
        var batch = _terminalOutputBuffer.Drain();
        if (_isClosing || Dispatcher.HasShutdownStarted) return;

        var outputByProcess = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        void Append(string processId, string text)
        {
            if (!outputByProcess.TryGetValue(processId, out var builder))
                outputByProcess[processId] = builder = new StringBuilder();
            builder.Append(text);
        }

        foreach (var truncatedProcessId in batch.TruncatedProcessIds)
        {
            // After dropped bytes, a previously incomplete escape sequence is no longer reliable.
            if (_sessions.TryGetValue(truncatedProcessId, out var truncatedTab))
                truncatedTab.SequenceFilter.Reset();
            Append(truncatedProcessId, "\r\n[часть terminal output пропущена из-за переполнения буфера]\r\n");
        }
        foreach (var output in batch.Items)
        {
            if (_sessions.TryGetValue(output.ProcessId, out var tab))
                Append(output.ProcessId, tab.SequenceFilter.Strip(output.Text));
        }

        foreach (var (processId, text) in outputByProcess)
            if (_sessions.TryGetValue(processId, out var tab)) AppendOutput(tab, text.ToString());
    }

    private void OnSessionExited(CodexTerminalExit exit)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            _ = Dispatcher.BeginInvoke((Action)(() =>
            {
                // The notification can be queued before the window closes and delivered afterward.
                if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished
                    || !_sessions.TryGetValue(exit.ProcessId, out var tab)) return;
                tab.IsRunning = false;
                tab.HeaderLabel.Text = tab.Title + " · завершён";
                if (!string.IsNullOrWhiteSpace(exit.Error)) AppendOutput(tab, "\r\n[ошибка терминала: " + exit.Error + "]\r\n");
                else AppendOutput(tab, $"\r\n[PowerShell завершён, код {exit.ExitCode}]\r\n");
                _status.Text = "Командная оболочка завершена";
            }));
        }
        catch (InvalidOperationException)
        {
            // The dispatcher may shut down between checking its state and scheduling the callback.
        }
    }

    private static void AppendOutput(TerminalTab tab, string text)
    {
        if (tab.Output is null || text.Length == 0) return;
        tab.Output.AppendText(text);
        if (tab.Output.Text.Length > 400_000)
            tab.Output.Text = tab.Output.Text[^300_000..];
        tab.Output.CaretIndex = tab.Output.Text.Length;
        tab.Output.ScrollToEnd();
    }

    // One filter per terminal, used only by the UI dispatcher while draining output.
    // State must survive chunk boundaries (including an OSC terminator split after ESC).
    private sealed class TerminalSequenceFilter
    {
        private bool _escape;
        private bool _csi;
        private bool _osc;
        private bool _oscEscape;

        public void Reset()
        {
            _escape = _csi = _osc = _oscEscape = false;
        }

        public string Strip(string text)
        {
            var output = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (_osc)
                {
                    if (ch == '\a') { _osc = false; _oscEscape = false; }
                    else if (_oscEscape && ch == '\\') { _osc = false; _oscEscape = false; }
                    else _oscEscape = ch == '\x1b';
                    continue;
                }
                if (_csi)
                {
                    if (ch is >= '\x40' and <= '\x7e') _csi = false;
                    continue;
                }
                if (_escape)
                {
                    _escape = false;
                    if (ch == '[') _csi = true;
                    else if (ch == ']') _osc = true;
                    continue;
                }
                if (ch == '\x1b') { _escape = true; continue; }
                if (ch == '\0' || ch < ' ' && ch is not ('\r' or '\n' or '\t' or '\b')) continue;
                output.Append(ch);
            }
            return output.ToString();
        }

    }

    private static Button MakeButton(string label, bool accent = false) => new()
    {
        Content = label,
        Padding = new Thickness(10, 6, 10, 6),
        Background = Brush(accent ? "#223B38" : "#263344"),
        Foreground = Brush(accent ? "#92E5C7" : "#D3DCE6"),
        BorderBrush = Brush("#34373E"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private sealed class TerminalTab(string processId, string title)
    {
        public string ProcessId { get; } = processId;
        public string Title { get; } = title;
        public TabItem Item { get; set; } = null!;
        public TextBlock HeaderLabel { get; set; } = null!;
        public TextBox Output { get; set; } = null!;
        public TextBox Input { get; set; } = null!;
        public bool IsRunning { get; set; }
        public TerminalSequenceFilter SequenceFilter { get; } = new();
        public List<string> History { get; } = [];
        public int HistoryIndex { get; set; }
        public string HistoryDraft { get; set; } = "";
    }
}
