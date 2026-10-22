using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class CodexMcpServerStatusWindow : Window
{
    private readonly Func<Task<IReadOnlyList<CodexMcpServerStatus>>> _loadStatuses;
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
    private IReadOnlyList<CodexMcpServerStatus> _items = [];
    private bool _busy;

    public CodexMcpServerStatusWindow(Func<Task<IReadOnlyList<CodexMcpServerStatus>>> loadStatuses)
    {
        _loadStatuses = loadStatuses;
        Title = "MCP-серверы Codex";
        Width = 840;
        Height = 600;
        MinWidth = 680;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
        _servers.SelectionChanged += (_, _) => ShowSelectedDetails();
        _refresh.Click += async (_, _) => await ReloadAsync();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_refresh, Dock.Right);
        header.Children.Add(_refresh);
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
            Text = "Состояние загружается из общего профиля Codex. App Server может запустить настроенные серверы и запросить список инструментов; конфигурация и OAuth здесь не меняются.",
            Foreground = Brush("#A7B5C0"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(note, 1);
        root.Children.Add(note);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
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
        Grid.SetRow(content, 2);
        root.Children.Add(content);

        Grid.SetRow(_status, 3);
        root.Children.Add(_status);
        return root;
    }

    private async Task ReloadAsync()
    {
        if (_busy) return;
        _busy = true;
        _refresh.IsEnabled = false;
        _status.Text = "Проверка MCP-серверов…";
        try
        {
            _items = await _loadStatuses();
            _empty.Text = "В общем профиле Codex нет настроенных MCP-серверов.";
            RefreshList();
            _status.Text = _items.Count == 0
                ? "Проверка завершена. Настроенных MCP-серверов нет."
                : $"Проверка завершена. Серверов: {_items.Count}.";
        }
        catch (Exception error)
        {
            _items = [];
            _empty.Text = "Список MCP-серверов не загружен.";
            RefreshList();
            _details.Text = "Не удалось получить состояние MCP-серверов.";
            _status.Text = "Проверка не удалась: " + error.Message;
        }
        finally
        {
            _busy = false;
            _refresh.IsEnabled = true;
        }
    }

    private void RefreshList()
    {
        _servers.Items.Clear();
        foreach (var status in _items)
        {
            var labels = new StackPanel();
            labels.Children.Add(new TextBlock
            {
                Text = status.Name,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            labels.Children.Add(new TextBlock
            {
                Text = $"{status.StateLabel} · инструментов: {status.ToolNames.Count}",
                Foreground = Brush("#A7B5C0"),
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 0)
            });
            _servers.Items.Add(new ListBoxItem
            {
                Tag = status,
                Content = labels,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 5),
                Background = Brush("#16212B"),
                BorderBrush = Brush("#2A3946"),
                BorderThickness = new Thickness(1)
            });
        }
        _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count > 0) _servers.SelectedIndex = 0;
        else _details.Text = "Выберите сервер, чтобы посмотреть его авторизацию и инструменты.";
    }

    private void ShowSelectedDetails()
    {
        if ((_servers.SelectedItem as ListBoxItem)?.Tag is not CodexMcpServerStatus status) return;
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

    private static Button MakeButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(12, 7, 12, 7),
        Background = Brush("#16212B"),
        Foreground = Brush("#ECF2F5"),
        BorderBrush = Brush("#2A3946"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
