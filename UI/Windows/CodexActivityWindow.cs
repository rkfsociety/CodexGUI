using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly TextBox _search = new();
    private readonly Button _clearSearch = new() { Content = "Очистить", IsEnabled = false };
    private IReadOnlyList<CodexActivityItem> _allItems = Array.Empty<CodexActivityItem>();

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
        AutomationProperties.SetLiveSetting(_summary, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_summary, "Количество чатов в списке активности");
        _search.TextChanged += (_, _) =>
        {
            _clearSearch.IsEnabled = _search.Text.Length > 0;
            RenderItems(resetScroll: true);
        };
        _clearSearch.Click += (_, _) => _search.Clear();
        _search.PreviewKeyDown += (_, e) =>
        {
            if ((e.Key == Key.Enter || e.Key == Key.Down) && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_items.Children.OfType<Button>().FirstOrDefault() is { } first)
                {
                    first.Focus();
                    e.Handled = true;
                }
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                _search.Focus();
                _search.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _search.Text.Length > 0)
            {
                _search.Clear();
                e.Handled = true;
            }
        };
    }

    public void UpdateItems(IReadOnlyList<CodexActivityItem> items)
    {
        _allItems = new List<CodexActivityItem>(items);
        RenderItems();
    }

    private void RenderItems(bool resetScroll = false)
    {
        var scrollOffset = resetScroll ? 0 : _scroll.VerticalOffset;
        // Keep keyboard navigation on the same chat when live activity refreshes the list.
        var focusedThreadId = _items.Children.OfType<Button>()
            .FirstOrDefault(button => button.IsKeyboardFocusWithin)?.Tag as string;
        _items.Children.Clear();
        var query = _search.Text.Trim();
        var filtered = new List<CodexActivityItem>();
        foreach (var item in _allItems)
            if (query.Length == 0 || Matches(item, query)) filtered.Add(item);

        _summary.Text = _allItems.Count == 0
            ? "Сейчас нет активных чатов, запросов на решение или непрочитанных ответов."
            : query.Length == 0
                ? $"Чатов в списке активности: {_allItems.Count}"
                : $"Найдено: {filtered.Count} из {_allItems.Count}";

        if (filtered.Count == 0)
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
                    Text = _allItems.Count == 0
                        ? "Когда Codex завершит фоновый ход или запросит решение, чат появится здесь."
                        : "По вашему запросу ничего не найдено. Измените или очистите поиск.",
                    Foreground = Brush("#B0B5BE"),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20
                }
            });
            if (resetScroll) _scroll.ScrollToTop();
            else RestoreScrollOffset(scrollOffset);
            return;
        }

        foreach (var item in filtered)
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(13, 12, 13, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Background = Brush("#1D1F23"),
                BorderBrush = Brush("#34373E"),
                BorderThickness = new Thickness(1),
                ToolTip = $"{item.Status} · {item.ProjectName}",
                Tag = item.ThreadId
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
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 44,
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
                try
                {
                    _openThread(item.ThreadId);
                }
                catch (Exception error)
                {
                    CodexMessageDialog.Show(this, "Не удалось открыть чат: " + error.Message,
                        "Ошибка перехода", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                Close();
            };
            _items.Children.Add(button);
        }
        if (resetScroll) _scroll.ScrollToTop();
        else RestoreScrollOffset(scrollOffset);
        if (focusedThreadId is not null)
        {
            var previousSelection = _items.Children.OfType<Button>()
                .FirstOrDefault(button => string.Equals(button.Tag as string, focusedThreadId, StringComparison.Ordinal));
            if (previousSelection is not null)
                Dispatcher.BeginInvoke(() => previousSelection.Focus(), DispatcherPriority.Loaded);
        }
    }

    private static bool Matches(CodexActivityItem item, string query)
    {
        if (item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.ProjectName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var activity in item.RecentEvents)
            if (activity.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || activity.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
        var searchRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _search.Height = 34;
        _search.MaxLength = 512;
        _search.Padding = new Thickness(10, 6, 10, 6);
        _search.Background = Brush("#181A1E");
        _search.Foreground = Brush("#F1F3F5");
        _search.BorderBrush = Brush("#34373E");
        _search.ToolTip = "Поиск по чатам, проектам, состояниям и событиям (Ctrl+F)";
        AutomationProperties.SetName(_search, "Поиск по активности чатов");
        searchRow.Children.Add(_search);
        _clearSearch.Margin = new Thickness(8, 0, 0, 0);
        _clearSearch.Padding = new Thickness(12, 6, 12, 6);
        _clearSearch.Background = Brush("#263344");
        _clearSearch.Foreground = Brush("#D3DCE6");
        _clearSearch.BorderBrush = Brush("#34373E");
        Grid.SetColumn(_clearSearch, 1);
        searchRow.Children.Add(_clearSearch);
        Grid.SetRow(searchRow, 2);
        root.Children.Add(searchRow);
        AutomationProperties.SetName(_scroll, "Результаты поиска по активности");
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Content = _items;
        Grid.SetRow(_scroll, 3);
        root.Children.Add(_scroll);
        return root;
    }

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
