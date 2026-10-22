using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed record CodexSubagentInfo(string ThreadId, string Tool, string? Prompt, string Status, string? Message);

internal sealed class CodexSubagentsWindow : Window
{
    private readonly Func<IReadOnlyList<CodexSubagentInfo>> _readSubagents;
    private readonly Func<string, Task> _openSubagent;
    private readonly ListBox _list = new() { Background = Brush("#171E27"), BorderThickness = new Thickness(0) };
    private readonly TextBlock _details = new();
    private readonly Button _openButton = new() { Content = "Открыть историю субагента" };

    public CodexSubagentsWindow(string parentTitle, Func<IReadOnlyList<CodexSubagentInfo>> readSubagents,
        Func<string, Task> openSubagent)
    {
        _readSubagents = readSubagents;
        _openSubagent = openSubagent;
        Title = "Субагенты · " + parentTitle;
        Width = 900;
        Height = 620;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#11151B");
        Foreground = Brush("#F0F0F1");
        FontFamily = new FontFamily("Segoe UI");
        Content = BuildLayout();
        _list.SelectionChanged += (_, _) => UpdateDetails();
        RefreshList();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "Субагенты", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 4) });
        var note = new TextBlock
        {
            Text = "Подзадачи и состояние агентов, вызванных текущим чатом. Откройте агента, чтобы читать его историю и продолжить работу в отдельном чате.",
            Foreground = Brush("#A6B1BF"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 13)
        };
        Grid.SetRow(note, 1);
        root.Children.Add(note);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(content, 2);
        root.Children.Add(content);
        content.Children.Add(Card(_list));
        var detailsPanel = new StackPanel { Margin = new Thickness(16, 4, 0, 0) };
        detailsPanel.Children.Add(new TextBlock
        {
            Text = "ЗАДАЧА АГЕНТА",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#8795A6"),
            Margin = new Thickness(0, 0, 0, 8)
        });
        _details.TextWrapping = TextWrapping.Wrap;
        _details.Foreground = Brush("#D3DCE6");
        _details.MinHeight = 80;
        detailsPanel.Children.Add(_details);
        _openButton.Margin = new Thickness(0, 12, 0, 0);
        _openButton.Padding = new Thickness(11, 7, 11, 7);
        _openButton.Background = Brush("#223B38");
        _openButton.Foreground = Brush("#A8E6CF");
        _openButton.Click += async (_, _) =>
        {
            if (SelectedSubagent() is not { } agent) return;
            _openButton.IsEnabled = false;
            try
            {
                await _openSubagent(agent.ThreadId);
                DialogResult = true;
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Не удалось открыть субагента", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _openButton.IsEnabled = SelectedSubagent() is not null; }
        };
        detailsPanel.Children.Add(_openButton);
        var detailsCard = Card(detailsPanel);
        Grid.SetColumn(detailsCard, 1);
        content.Children.Add(detailsCard);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var refresh = MakeButton("Обновить");
        refresh.Click += (_, _) => RefreshList();
        footer.Children.Add(refresh);
        var close = MakeButton("Закрыть");
        close.Margin = new Thickness(8, 0, 0, 0);
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private void RefreshList()
    {
        var selectedThreadId = SelectedSubagent()?.ThreadId;
        var subagents = _readSubagents();
        _list.Items.Clear();
        foreach (var agent in subagents)
        {
            var card = new StackPanel();
            card.Children.Add(new TextBlock
            {
                Text = $"{StatusName(agent.Status)} · {ToolName(agent.Tool)}",
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(agent.Status is "errored" or "notFound" ? "#F08C8C" : agent.Status == "running" ? "#A8E6CF" : "#D3DCE6")
            });
            card.Children.Add(new TextBlock
            {
                Text = agent.Prompt ?? ShortId(agent.ThreadId),
                FontSize = 10,
                Foreground = Brush("#A6B1BF"),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 36,
                Margin = new Thickness(0, 4, 0, 0)
            });
            var item = new ListBoxItem { Tag = agent, Padding = new Thickness(10, 8, 10, 8), Content = card };
            _list.Items.Add(item);
            if (agent.ThreadId == selectedThreadId) _list.SelectedItem = item;
        }
        if (_list.SelectedItem is null && _list.Items.Count > 0) _list.SelectedIndex = 0;
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        var agent = SelectedSubagent();
        _openButton.IsEnabled = agent is not null;
        if (agent is null)
        {
            _details.Text = "Здесь появятся вызванные субагенты. После обновления истории нажмите «Обновить».";
            return;
        }
        _details.Text = $"Состояние: {StatusName(agent.Status)}\nОперация: {ToolName(agent.Tool)}\nID: {agent.ThreadId}"
            + (string.IsNullOrWhiteSpace(agent.Prompt) ? "" : $"\n\nЗадание:\n{agent.Prompt}")
            + (string.IsNullOrWhiteSpace(agent.Message) ? "" : $"\n\nПоследнее состояние:\n{agent.Message}");
    }

    private CodexSubagentInfo? SelectedSubagent() => (_list.SelectedItem as ListBoxItem)?.Tag as CodexSubagentInfo;

    private static Border Card(UIElement child) => new()
    {
        Background = Brush("#171E27"),
        BorderBrush = Brush("#2B3746"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(5),
        Child = child
    };

    private static Button MakeButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(11, 7, 11, 7),
        Background = Brush("#263344"),
        Foreground = Brush("#D3DCE6"),
        BorderBrush = Brush("#34465A"),
        BorderThickness = new Thickness(1)
    };

    private static string ToolName(string tool) => tool switch
    {
        "spawn_agent" or "spawnAgent" => "Запуск",
        "send_input" or "sendInput" => "Сообщение",
        "wait" => "Ожидание",
        "close_agent" or "closeAgent" => "Завершение",
        _ => tool
    };

    private static string StatusName(string status) => status switch
    {
        "pendingInit" => "Запускается",
        "running" or "inProgress" => "Работает",
        "interrupted" => "Прерван",
        "completed" => "Завершён",
        "errored" or "failed" => "Ошибка",
        "shutdown" => "Остановлен",
        "notFound" => "Не найден",
        _ => status
    };

    private static string ShortId(string id) => id.Length > 18 ? id[..8] + "…" + id[^6..] : id;
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
