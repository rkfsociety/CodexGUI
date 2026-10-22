using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class CodexMcpServerStatusWindow : Window
{
    private readonly string _appServerExecutablePath;
    private readonly TextBox _search = new()
    {
        Height = 34,
        Padding = new Thickness(10, 7, 10, 7),
        Background = Brush("#16212B"),
        Foreground = Brush("#ECF2F5"),
        BorderBrush = Brush("#2A3946"),
        ToolTip = "Поиск по имени сервера, инструментам, статусу и адресу"
    };
    private readonly ListBox _servers = new() { Background = Brush("#101820"), BorderBrush = Brush("#2A3946") };
    private readonly TextBlock _empty = new()
    {
        Text = "В общем профиле Codex нет настроенных MCP-серверов.",
        Foreground = Brush("#A7B5C0"),
        Margin = new Thickness(12),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock _details = new()
    {
        Text = "Выберите сервер, чтобы посмотреть его авторизацию и инструменты.",
        Foreground = Brush("#A7B5C0"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(16)
    };
    private readonly TextBlock _status = new()
    {
        Foreground = Brush("#A7B5C0"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly Button _refresh = MakeButton("Проверить снова");
    private readonly Button _login = MakeButton("Войти", accent: true);
    private IReadOnlyList<CodexMcpServerStatus> _items = [];
    private bool _loadFailed;
    private bool _busy;

    public CodexMcpServerStatusWindow(string appServerExecutablePath)
    {
        _appServerExecutablePath = appServerExecutablePath;
        Title = "MCP-серверы Codex";
        Width = 840;
        Height = 600;
        MinWidth = 680;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
        VirtualizingPanel.SetIsVirtualizing(_servers, true);
        VirtualizingPanel.SetVirtualizationMode(_servers, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_servers, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_servers, ScrollBarVisibility.Disabled);
        _servers.SelectionChanged += (_, _) =>
        {
            ShowSelectedDetails();
            UpdateButtons();
        };
        _search.TextChanged += (_, _) => RefreshList(SelectedServer()?.Name);
        _refresh.Click += async (_, _) => await ReloadAsync();
        _login.Click += async (_, _) => await LoginSelectedAsync();
        Loaded += async (_, _) => await ReloadAsync();
        UpdateButtons();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _login.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(actions, Dock.Right);
        actions.Children.Add(_refresh);
        actions.Children.Add(_login);
        header.Children.Add(actions);
        header.Children.Add(new TextBlock
        {
            Text = "MCP-серверы Codex",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(header);

        var note = new TextBlock
        {
            Text = "Состояние загружается из общего профиля Codex. App Server может запустить настроенные серверы и запросить список инструментов. Кнопка «Войти» появляется для HTTP-серверов, которым требуется авторизация; страницу входа нужно открыть отдельно.",
            Foreground = Brush("#A7B5C0"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(note, 1);
        root.Children.Add(note);

        var searchRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.Children.Add(new TextBlock
        {
            Text = "Фильтр",
            Foreground = Brush("#A7B5C0"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        Grid.SetColumn(_search, 1);
        searchRow.Children.Add(_search);
        Grid.SetRow(searchRow, 2);
        root.Children.Add(searchRow);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        _servers.ItemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel>
                <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                <TextBlock Text="{Binding ListSummary}" Foreground="#A7B5C0" FontSize="10" Margin="0,3,0,0" />
              </StackPanel>
            </DataTemplate>
            """);
        var listArea = new Grid { Margin = new Thickness(0, 0, 10, 0) };
        listArea.Children.Add(_servers);
        listArea.Children.Add(_empty);
        content.Children.Add(listArea);
        var detailsFrame = new Border
        {
            Background = Brush("#101820"),
            BorderBrush = Brush("#2A3946"),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                Content = _details,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
        Grid.SetColumn(detailsFrame, 1);
        content.Children.Add(detailsFrame);
        Grid.SetRow(content, 3);
        root.Children.Add(content);

        Grid.SetRow(_status, 4);
        root.Children.Add(_status);
        return root;
    }

    private async Task ReloadAsync(bool keepBusy = false)
    {
        if (_busy && !keepBusy) return;
        var wasBusy = _busy;
        var selectedName = SelectedServer()?.Name;
        _busy = true;
        UpdateButtons();
        _status.Text = "Проверка MCP-серверов…";
        try
        {
            _items = await CodexMcpServerStatusCatalog.LoadAsync(new CodexAppServerClient(_appServerExecutablePath));
            _loadFailed = false;
            RefreshList(selectedName);
            _status.Text = _items.Count == 0
                ? "Проверка завершена. Настроенных MCP-серверов нет."
                : $"Проверка завершена. Серверов: {_items.Count}.";
        }
        catch (Exception error)
        {
            _items = [];
            _loadFailed = true;
            RefreshList();
            _details.Text = "Не удалось получить состояние MCP-серверов.";
            _status.Text = "Проверка не удалась: " + error.Message;
        }
        finally
        {
            _busy = wasBusy;
            UpdateButtons();
        }
    }

    private async Task LoginSelectedAsync()
    {
        if (_busy || SelectedServer() is not { } server || !RequiresLogin(server)) return;
        _busy = true;
        UpdateButtons();
        _status.Text = $"Подготовка входа в «{server.Name}»…";
        CodexMcpServerOAuthLoginWindow? loginWindow = null;
        try
        {
            (bool Success, string? Error) outcome;
            await using (var connection = await CodexAppServerConnection.StartAsync(_appServerExecutablePath))
            {
                await connection.InitializeAsync();
                var response = await connection.SendRequestAsync("mcpServer/oauth/login", new
                {
                    name = server.Name,
                    timeoutSecs = 300
                });
                var authorizationUrl = ReadRequiredString(response, "authorizationUrl");
                var loginId = ReadOptionalString(response, "loginId");
                loginWindow = new CodexMcpServerOAuthLoginWindow(server.Name, authorizationUrl) { Owner = this };
                loginWindow.Show();

                using var waitTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                var completionTask = WaitForOAuthCompletionAsync(connection, server.Name, loginId, waitTimeout.Token);
                var completed = await Task.WhenAny(completionTask, loginWindow.Cancelled);
                if (completed != completionTask)
                {
                    waitTimeout.Cancel();
                    try { await completionTask; }
                    catch (OperationCanceledException) { }
                    _status.Text = "Вход отменён. При необходимости запустите его снова.";
                    return;
                }

                outcome = await completionTask;
            }

            if (loginWindow?.IsVisible == true) loginWindow.Close();
            if (!outcome.Success)
            {
                _status.Text = "Вход не завершён: " + (outcome.Error ?? "сервер не подтвердил авторизацию.");
                await ReloadAsync(keepBusy: true);
                return;
            }

            _status.Text = "Вход завершён. Обновляю состояние MCP-серверов…";
            await ReloadAsync(keepBusy: true);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Время ожидания входа истекло.";
        }
        catch (Exception error)
        {
            _status.Text = "Не удалось выполнить вход: " + error.Message;
        }
        finally
        {
            if (loginWindow?.IsVisible == true) loginWindow.Close();
            _busy = false;
            UpdateButtons();
        }
    }

    private static async Task<(bool Success, string? Error)> WaitForOAuthCompletionAsync(
        CodexAppServerConnection connection,
        string serverName,
        string? loginId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (connection.TryReadIncoming(out var message))
            {
                if (ReadString(message, "method") != "mcpServer/oauthLogin/completed"
                    || !message.TryGetProperty("params", out var parameters)
                    || ReadString(parameters, "name") != serverName)
                    continue;

                var notificationLoginId = ReadOptionalString(parameters, "loginId");
                if (loginId is not null && notificationLoginId is not null
                    && !string.Equals(loginId, notificationLoginId, StringComparison.Ordinal))
                    continue;
                if (!parameters.TryGetProperty("success", out var success)
                    || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Codex App Server вернул некорректный результат OAuth-входа.");
                return (success.GetBoolean(), ReadOptionalString(parameters, "error"));
            }

            if (connection.ReaderTask.IsCompleted)
                throw new EndOfStreamException("Codex App Server завершился до окончания OAuth-входа.");
            if (!await connection.WaitForIncomingAsync(cancellationToken))
                throw new EndOfStreamException("Codex App Server завершил поток OAuth-уведомлений.");
        }
    }

    private void RefreshList(string? preferredName = null)
    {
        var query = _search.Text.Trim();
        var visibleItems = _items.Where(status => MatchesSearch(status, query))
            .ToArray();
        _servers.ItemsSource = visibleItems;
        _empty.Text = _loadFailed
            ? "Список MCP-серверов не загружен."
            : _items.Count == 0
                ? "В общем профиле Codex нет настроенных MCP-серверов."
                : $"По фильтру «{query}» серверы не найдены.";
        _empty.Visibility = visibleItems.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visibleItems.Length > 0)
        {
            var selectedIndex = 0;
            if (preferredName is not null)
            {
                for (var index = 0; index < visibleItems.Length; index++)
                {
                    if (string.Equals(visibleItems[index].Name, preferredName, StringComparison.Ordinal))
                    {
                        selectedIndex = index;
                        break;
                    }
                }
            }
            _servers.SelectedIndex = selectedIndex;
        }
        else _details.Text = _loadFailed
            ? "Не удалось получить состояние MCP-серверов."
            : _items.Count == 0
                ? "В общем профиле Codex нет настроенных MCP-серверов."
                : "Нет серверов, соответствующих фильтру.";
    }

    private static bool MatchesSearch(CodexMcpServerStatus status, string query)
    {
        if (query.Length == 0) return true;
        if (Contains(status.Name, query)
            || Contains(status.ServerTitle, query)
            || Contains(status.ServerVersion, query)
            || Contains(status.StateLabel, query)
            || Contains(status.AuthenticationLabel, query)
            || Contains(status.HttpOrigin, query)
            || Contains(status.ToolsError, query))
            return true;
        foreach (var toolName in status.ToolNames)
            if (Contains(toolName, query)) return true;
        return false;
    }

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private void ShowSelectedDetails()
    {
        if (_servers.SelectedItem is not CodexMcpServerStatus status)
        {
            _details.Text = "Выберите сервер, чтобы посмотреть его авторизацию и инструменты.";
            return;
        }
        var lines = new List<string>
        {
            status.ServerTitle is { Length: > 0 } title ? $"{title} · {status.Name}" : status.Name,
            $"Состояние: {status.StateLabel}",
            $"Авторизация: {status.AuthenticationLabel}",
            $"Инструменты ({status.ToolNames.Count}):"
        };
        lines.AddRange(status.ToolNames.Select(name => "• " + name));
        if (status.ServerVersion is { Length: > 0 } version) lines.Add("Версия сервера: " + version);
        if (status.HttpOrigin is { Length: > 0 } origin) lines.Add("Адрес: " + origin);
        if (!string.IsNullOrWhiteSpace(status.ToolsError)) lines.Add("Ошибка: " + status.ToolsError);
        _details.Text = string.Join(Environment.NewLine, lines);
    }

    private void UpdateButtons()
    {
        _refresh.IsEnabled = !_busy;
        _servers.IsEnabled = !_busy;
        _search.IsEnabled = !_busy;
        var canLogin = SelectedServer() is { } status && RequiresLogin(status);
        _login.Visibility = canLogin ? Visibility.Visible : Visibility.Collapsed;
        _login.IsEnabled = !_busy && canLogin;
    }

    private CodexMcpServerStatus? SelectedServer()
        => _servers.SelectedItem as CodexMcpServerStatus;

    private static bool RequiresLogin(CodexMcpServerStatus status)
        => !string.IsNullOrWhiteSpace(status.HttpOrigin)
           && status.RuntimeStatus != "disabled"
           && (status.AuthStatus == "notLoggedIn" || status.RuntimeStatus == "authenticationRequired");

    private static string ReadRequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"Codex App Server не вернул поле {name} для OAuth-входа.");
        return property.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Поле {name} в ответе OAuth-входа должно быть строкой или null.");
        return property.GetString();
    }

    private static string? ReadString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static Button MakeButton(string text, bool accent = false) => new()
    {
        Content = text,
        Padding = new Thickness(12, 7, 12, 7),
        Background = Brush(accent ? "#223B38" : "#16212B"),
        Foreground = Brush(accent ? "#92E5C7" : "#ECF2F5"),
        BorderBrush = Brush("#2A3946"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
