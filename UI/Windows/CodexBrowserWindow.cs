using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CodexGUI;

internal sealed class CodexBrowserWindow : Window
{
    private const string HomeUrl = "https://www.google.com/";
    private readonly TabControl _tabs = new();
    private readonly TextBox _addressBar = new();
    private readonly Button _backButton = MakeButton("←", "Назад");
    private readonly Button _forwardButton = MakeButton("→", "Вперёд");
    private readonly Button _reloadButton = MakeButton("↻", "Обновить страницу");
    private readonly Button _stopButton = MakeButton("×", "Остановить загрузку");
    private readonly Button _newTabButton = MakeButton("＋", "Новая вкладка");
    private readonly TextBlock _status = new();
    private readonly string _userDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "browser");
    private Task<CoreWebView2Environment>? _environmentTask;

    public CodexBrowserWindow()
    {
        Title = "CodexGUI — браузер";
        Width = 1180;
        Height = 820;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();

        _backButton.Click += (_, _) => ActiveBrowser?.GoBack();
        _forwardButton.Click += (_, _) => ActiveBrowser?.GoForward();
        _reloadButton.Click += (_, _) => ActiveBrowser?.Reload();
        _stopButton.Click += (_, _) => ActiveBrowser?.CoreWebView2?.Stop();
        _newTabButton.Click += async (_, _) => await CreateTabAsync(HomeUrl);
        _tabs.SelectionChanged += (_, _) => UpdateToolbar();
        _addressBar.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            NavigateAddress();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += async (_, _) => await CreateTabAsync(HomeUrl);
        Closed += (_, _) =>
        {
            foreach (var tab in _tabs.Items.OfType<TabItem>())
                (tab.Tag as WebView2)?.Dispose();
            _tabs.Items.Clear();
        };
    }

    private WebView2? ActiveBrowser => (_tabs.SelectedItem as TabItem)?.Tag as WebView2;

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 9) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { _backButton, _forwardButton, _reloadButton, _stopButton, _newTabButton })
        {
            button.Margin = new Thickness(0, 0, 6, 0);
            actions.Children.Add(button);
        }
        DockPanel.SetDock(actions, Dock.Left);
        toolbar.Children.Add(actions);
        var go = MakeButton("Перейти", "Открыть адрес или выполнить поиск");
        go.Margin = new Thickness(8, 0, 0, 0);
        go.Click += (_, _) => NavigateAddress();
        DockPanel.SetDock(go, Dock.Right);
        toolbar.Children.Add(go);
        _addressBar.MinHeight = 36;
        _addressBar.VerticalContentAlignment = VerticalAlignment.Center;
        _addressBar.Padding = new Thickness(10, 5, 10, 5);
        _addressBar.Background = Brush("#11181E");
        _addressBar.Foreground = Brush("#ECF2F5");
        _addressBar.BorderBrush = Brush("#435D6D");
        _addressBar.BorderThickness = new Thickness(1);
        _addressBar.FontSize = 13;
        _addressBar.ToolTip = "Введите адрес сайта или поисковый запрос";
        AutomationProperties.SetName(_addressBar, "Адрес сайта или поисковый запрос");
        toolbar.Children.Add(_addressBar);
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        _tabs.MinHeight = 34;
        AutomationProperties.SetName(_tabs, "Вкладки браузера");
        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);

        _status.Text = "Открывается браузер…";
        _status.Foreground = Brush("#9AA8B8");
        _status.FontSize = 11;
        _status.Margin = new Thickness(2, 7, 2, 0);
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);
        return root;
    }

    private async Task CreateTabAsync(string? initialUrl = null)
    {
        var browser = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(11, 17, 23) };
        var tab = new TabItem { Content = browser, Tag = browser };
        tab.Header = CreateTabHeader("Новая вкладка", tab);
        AutomationProperties.SetName(tab, "Новая вкладка");
        _tabs.Items.Add(tab);
        _tabs.SelectedItem = tab;
        _status.Text = "Подключение к Microsoft Edge WebView2…";
        UpdateToolbar();

        try
        {
            Directory.CreateDirectory(_userDataFolder);
            _environmentTask ??= CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            var environment = await _environmentTask;
            // The user may close this tab while WebView2 is still starting.
            if (!_tabs.Items.Contains(tab)) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (!_tabs.Items.Contains(tab)) return;
            var core = browser.CoreWebView2;
            core.NewWindowRequested += async (_, e) =>
            {
                e.Handled = true;
                await CreateTabAsync(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (!IsAllowedNavigation(e.Uri)) e.Cancel = true;
                if (ReferenceEquals(ActiveBrowser, browser)) _status.Text = "Загрузка страницы…";
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (!ReferenceEquals(ActiveBrowser, browser)) return;
                _status.Text = e.IsSuccess ? "Готово" : $"Не удалось загрузить страницу: {e.WebErrorStatus}";
                UpdateToolbar();
                UpdateAddress(browser);
            };
            core.SourceChanged += (_, _) => UpdateAddress(browser);
            core.DocumentTitleChanged += (_, _) => UpdateTabTitle(tab, browser);
            core.ProcessFailed += (_, e) =>
            {
                if (ReferenceEquals(ActiveBrowser, browser)) _status.Text = $"Ошибка WebView2: {e.ProcessFailedKind}";
            };
            if (initialUrl is not null) core.Navigate(initialUrl);
            _status.Text = "Готово";
            UpdateToolbar();
        }
        catch (Exception ex)
        {
            if (_environmentTask?.IsFaulted == true) _environmentTask = null;
            // A disposed tab must not be updated after its asynchronous startup fails.
            if (!_tabs.Items.Contains(tab)) return;
            _status.Text = $"WebView2 не удалось запустить: {ex.Message}";
            browser.Visibility = Visibility.Collapsed;
            var message = new TextBlock
            {
                Text = "Для встроенного браузера требуется Microsoft Edge WebView2 Runtime. Установите или восстановите WebView2 Runtime, затем перезапустите браузер.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(30),
                Foreground = Brush("#D8E3E9"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            tab.Content = message;
        }
    }

    private void NavigateAddress()
    {
        var uri = CreateNavigationUri(_addressBar.Text);
        if (uri is null)
        {
            _status.Text = "Введите адрес http(s) или поисковый запрос.";
            return;
        }
        _addressBar.Text = uri.AbsoluteUri;
        ActiveBrowser?.CoreWebView2?.Navigate(uri.AbsoluteUri);
    }

    internal static Uri? CreateNavigationUri(string? value)
    {
        var input = value?.Trim();
        if (string.IsNullOrEmpty(input)) return null;
        if (input.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(input, UriKind.Absolute, out var explicitUri) && IsAllowedNavigation(explicitUri.AbsoluteUri)
                ? explicitUri : null;
        if (input.Any(char.IsWhiteSpace)) return SearchUri(input);
        // Determine loopback status from the complete host, not a textual prefix:
        // "localhost.example.com" and "127.example.com" must use HTTPS.
        var hasCandidate = Uri.TryCreate("https://" + input, UriKind.Absolute, out var candidate);
        var host = hasCandidate ? candidate!.Host.Trim('[', ']') : string.Empty;
        var isLocalHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                          (System.Net.IPAddress.TryParse(host, out var address) &&
                           System.Net.IPAddress.IsLoopback(address));
        if (input.Contains(':') && !isLocalHost)
        {
            var schemeOrHost = input[..input.IndexOf(':')];
            if (!schemeOrHost.Contains('.')) return null;
        }
        if (input.Contains('.') || isLocalHost)
            return Uri.TryCreate((isLocalHost ? "http://" : "https://") + input, UriKind.Absolute, out var parsed) ? parsed : null;
        return SearchUri(input);
    }

    private static Uri SearchUri(string query) => new("https://www.google.com/search?q=" + Uri.EscapeDataString(query));

    private static bool IsAllowedNavigation(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         uri.AbsoluteUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase));

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (control && e.Key == Key.Tab)
        {
            // Cycle through tabs with the standard browser shortcuts, including wraparound.
            e.Handled = true;
            if (_tabs.Items.Count > 1)
            {
                var direction = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
                var current = Math.Max(0, _tabs.SelectedIndex);
                _tabs.SelectedIndex = (current + direction + _tabs.Items.Count) % _tabs.Items.Count;
            }
        }
        else if (control && e.Key == Key.T) { e.Handled = true; _ = CreateTabAsync(HomeUrl); }
        else if (control && e.Key == Key.W) { e.Handled = true; CloseCurrentTab(); }
        else if (control && e.Key == Key.L) { e.Handled = true; _addressBar.Focus(); _addressBar.SelectAll(); }
        else if (control && e.Key == Key.R || e.Key == Key.F5) { e.Handled = true; ActiveBrowser?.Reload(); }
        else if (e.Key == Key.Escape) { ActiveBrowser?.CoreWebView2?.Stop(); }
        else if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && e.Key == Key.Left) { e.Handled = true; ActiveBrowser?.GoBack(); }
        else if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && e.Key == Key.Right) { e.Handled = true; ActiveBrowser?.GoForward(); }
    }

    private void CloseCurrentTab()
    {
        if (_tabs.SelectedItem is TabItem tab) CloseTab(tab);
    }

    private void CloseTab(TabItem tab)
    {
        if (!_tabs.Items.Contains(tab)) return;
        var browser = tab.Tag as WebView2;
        _tabs.Items.Remove(tab);
        browser?.Dispose();
        if (_tabs.Items.Count == 0) Close();
        else UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        var core = ActiveBrowser?.CoreWebView2;
        _backButton.IsEnabled = core?.CanGoBack == true;
        _forwardButton.IsEnabled = core?.CanGoForward == true;
        _reloadButton.IsEnabled = core is not null;
        _stopButton.IsEnabled = core is not null;
        UpdateAddress(ActiveBrowser);
    }

    private void UpdateAddress(WebView2? browser)
    {
        if (browser is null || !ReferenceEquals(ActiveBrowser, browser) || _addressBar.IsKeyboardFocused) return;
        _addressBar.Text = browser.CoreWebView2?.Source ?? string.Empty;
    }

    private void UpdateTabTitle(TabItem tab, WebView2 browser)
    {
        var title = browser.CoreWebView2?.DocumentTitle;
        var label = string.IsNullOrWhiteSpace(title) ? "Новая вкладка" : title;
        tab.Header = CreateTabHeader(label, tab);
        AutomationProperties.SetName(tab, label);
    }

    private UIElement CreateTabHeader(string label, TabItem? tab)
    {
        var header = new DockPanel { LastChildFill = true, MinWidth = 100, MaxWidth = 240 };
        if (tab is not null)
        {
            var close = new Button
            {
                Content = "×", Width = 22, Height = 22, Padding = new Thickness(0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Brush("#9AA8B8"), ToolTip = "Закрыть вкладку"
            };
            AutomationProperties.SetName(close, $"Закрыть вкладку {label}");
            close.Click += (_, _) => CloseTab(tab);
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
        }
        header.Children.Add(new TextBlock
        {
            Text = label, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        });
        return header;
    }

    private static Button MakeButton(string text, string name)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 38,
            Height = 36,
            Padding = new Thickness(8, 3, 8, 3),
            Background = Brush("#14232C"),
            Foreground = Brush("#D8E3E9"),
            BorderBrush = Brush("#435D6D"),
            BorderThickness = new Thickness(1),
            ToolTip = name
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
