using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CodexGUI;

internal sealed record CodexActivityItem(
    string ThreadId,
    string Title,
    string ProjectName,
    string Status,
    string Description,
    string Accent);

internal sealed class CodexActivityWindow : Window
{
    private readonly Action<string> _openThread;
    private readonly TextBlock _summary = new();
    private readonly StackPanel _items = new();

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
        _items.Children.Clear();
        _summary.Text = items.Count switch
        {
            0 => "Сейчас нет активных чатов или непрочитанных ответов.",
            1 => "1 чат требует внимания",
            >= 2 and <= 4 => $"{items.Count} чата требуют внимания",
            _ => $"{items.Count} чатов требуют внимания"
        };

        if (items.Count == 0)
        {
            _items.Children.Add(new Border
            {
                Background = Brush("#16212B"),
                BorderBrush = Brush("#2A3946"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16),
                Child = new TextBlock
                {
                    Text = "Когда Codex завершит фоновый ход или запросит решение, чат появится здесь.",
                    Foreground = Brush("#A7B5C0"),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20
                }
            });
            return;
        }

        foreach (var item in items)
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(13, 12, 13, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Background = Brush("#16212B"),
                BorderBrush = Brush("#2A3946"),
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
                Foreground = Brush("#A7B5C0"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
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
        _summary.Foreground = Brush("#A7B5C0");
        _summary.FontSize = 11;
        _summary.Margin = new Thickness(0, 5, 0, 15);
        Grid.SetRow(_summary, 1);
        root.Children.Add(_summary);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _items
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        return root;
    }

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
