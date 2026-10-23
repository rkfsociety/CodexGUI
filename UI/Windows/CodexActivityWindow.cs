using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CodexGUI;

internal sealed record CodexActivityItem(
    string ThreadId,
    string Title,
    string ProjectName,
    string Status,
    string Description,
    string Accent)
{
    public IReadOnlyList<CodexActivityEvent> RecentEvents { get; init; } = [];
}

internal sealed record CodexActivityEvent(string Title, string Detail, bool IsError);

internal sealed class CodexActivityWindow : Window
{
    private readonly Action<string> _openThread;
    private readonly TextBlock _summary = new();
    private readonly StackPanel _items = new();
    private readonly ScrollViewer _scroll = new();

    public CodexActivityWindow(Action<string> openThread)
    {
        _openThread = openThread;
        Title = "Активность чатов";
        Width = 580;
        Height = 600;
        MinWidth = 460;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
    }

    public void UpdateItems(IReadOnlyList<CodexActivityItem> items)
    {
        var scrollOffset = _scroll.VerticalOffset;
        _items.Children.Clear();
        _summary.Text = items.Count == 0
            ? "Сейчас нет активных чатов, запросов на решение или непрочитанных ответов."
            : $"Чатов в списке активности: {items.Count}";

        if (items.Count == 0)
        {
            _items.Children.Add(new Border
            {
                Background = Brush("#1D1F23"),
                BorderBrush = Brush("#34373E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16),
                Child = new TextBlock
                {
                    Text = "Когда Codex завершит фоновый ход или запросит решение, чат появится здесь.",
                    Foreground = Brush("#B0B5BE"),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20
                }
            });
            RestoreScrollOffset(scrollOffset);
            return;
        }

        foreach (var item in items)
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(13, 12, 13, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Background = Brush("#1D1F23"),
                BorderBrush = Brush("#34373E"),
                BorderThickness = new Thickness(1),
                ToolTip = $"{item.Status} · {item.ProjectName}"
            };
            AutomationProperties.SetName(button, $"{item.Title}, {item.Status}, {item.ProjectName}");
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Brush(item.Accent),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 0, 0)
            });
            var labels = new StackPanel { Margin = new Thickness(8, 0, 10, 0) };
            labels.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            labels.Children.Add(new TextBlock
            {
                Text = item.ProjectName + " · " + item.Description,
                FontSize = 10,
                Foreground = Brush("#B0B5BE"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            if (item.RecentEvents.Count > 0)
            {
                var recentEvents = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                recentEvents.Children.Add(new TextBlock
                {
                    Text = "НЕДАВНИЕ ДЕЙСТВИЯ",
                    FontSize = 8,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("#7F8B9A")
                });
                foreach (var activity in item.RecentEvents)
                {
                    var eventRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                    eventRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                    eventRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    eventRow.Children.Add(new Ellipse
                    {
                        Width = 4,
                        Height = 4,
                        Fill = Brush(activity.IsError ? "#E18B84" : item.Accent),
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(1, 4, 0, 0)
                    });
                    var eventLabels = new StackPanel();
                    eventLabels.Children.Add(new TextBlock
                    {
                        Text = activity.Title,
                        FontSize = 9,
                        Foreground = Brush(activity.IsError ? "#E18B84" : "#B0B5BE"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    if (activity.Detail.Length > 0)
                        eventLabels.Children.Add(new TextBlock
                        {
                            Text = activity.Detail,
                            FontSize = 9,
                            Foreground = Brush("#81909C"),
                            TextWrapping = TextWrapping.Wrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxHeight = 28
                        });
                    Grid.SetColumn(eventLabels, 1);
                    eventRow.Children.Add(eventLabels);
                    recentEvents.Children.Add(eventRow);
                }
                labels.Children.Add(recentEvents);
            }
            Grid.SetColumn(labels, 1);
            row.Children.Add(labels);
            var status = new TextBlock
            {
                Text = item.Status,
                Foreground = Brush(item.Accent),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 2, 0, 0)
            };
            Grid.SetColumn(status, 2);
            row.Children.Add(status);
            button.Content = row;
            button.Click += (_, _) =>
            {
                Close();
                _openThread(item.ThreadId);
            };
            _items.Children.Add(button);
        }
        RestoreScrollOffset(scrollOffset);
    }

    private void RestoreScrollOffset(double offset)
    {
        if (offset <= 0 || !_scroll.IsLoaded) return;
        Dispatcher.BeginInvoke(() => _scroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TextBlock
        {
            Text = "Активность",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        _summary.Foreground = Brush("#B0B5BE");
        _summary.FontSize = 11;
        _summary.Margin = new Thickness(0, 5, 0, 15);
        Grid.SetRow(_summary, 1);
        root.Children.Add(_summary);
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Content = _items;
        Grid.SetRow(_scroll, 2);
        root.Children.Add(_scroll);
        return root;
    }

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
