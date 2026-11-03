using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class CodexMarketplaceManagerWindow : Window
{
    private readonly IList<CodexMarketplace> _marketplaces;
    private readonly Func<CodexMarketplace, Task> _removeMarketplace;
    private readonly ListBox _list = new() { Background = Brush("#151619"), BorderBrush = Brush("#34373E") };
    private readonly TextBlock _empty = new()
    {
        Text = "Настроенных маркетплейсов нет.",
        Foreground = Brush("#B0B5BE"),
        Margin = new Thickness(12)
    };
    private readonly TextBlock _status = new()
    {
        Foreground = Brush("#B0B5BE"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly Button _remove = new() { Content = "Удалить маркетплейс", IsEnabled = false };
    private readonly TextBox _search = new();
    private readonly Button _clearSearch = new() { Content = "Очистить", IsEnabled = false };
    private readonly TextBlock _count = new() { FontSize = 11, Foreground = Brush("#B0B5BE"), Margin = new Thickness(0, 0, 0, 8) };
    private bool _busy;

    public CodexMarketplaceManagerWindow(IReadOnlyList<CodexMarketplace> marketplaces,
        Func<CodexMarketplace, Task> removeMarketplace)
    {
        _marketplaces = marketplaces.ToList();
        _removeMarketplace = removeMarketplace;
        Title = "Маркетплейсы Codex";
        Width = 680;
        Height = 520;
        MinWidth = 540;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
        _list.SelectionChanged += (_, _) => UpdateButtons();
        AutomationProperties.SetName(_list, "Список маркетплейсов");
        AutomationProperties.SetName(_count, "Количество маркетплейсов");
        AutomationProperties.SetName(_status, "Результат удаления маркетплейса");
        _search.MaxLength = 512;
        _search.PreviewKeyDown += (_, e) =>
        {
            if ((e.Key == Key.Down || e.Key == Key.Enter) && _list.Items.Count > 0)
            {
                if (_list.SelectedIndex < 0) _list.SelectedIndex = 0;
                (_list.SelectedItem as ListBoxItem)?.Focus();
                e.Handled = true;
            }
        };
        _remove.Click += async (_, _) => await RemoveSelectedAsync();
        _search.TextChanged += (_, _) =>
        {
            _clearSearch.IsEnabled = _search.Text.Length > 0;
            RefreshList();
        };
        _clearSearch.Click += (_, _) => _search.Clear();
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
        RefreshList();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "Маркетплейсы Codex",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        var note = new TextBlock
        {
            Text = "Список загружается из Codex CLI. Удаление убирает запись из профиля Codex и локальный корень каталога, если он установлен.",
            Foreground = Brush("#B0B5BE"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 12)
        };
        Grid.SetRow(note, 1);
        root.Children.Add(note);

        var searchRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _search.Height = 34;
        _search.Padding = new Thickness(10, 6, 10, 6);
        _search.Background = Brush("#181A1E");
        _search.Foreground = Brush("#F1F3F5");
        _search.BorderBrush = Brush("#34373E");
        _search.ToolTip = "Поиск по названию или пути маркетплейса (Ctrl+F)";
        AutomationProperties.SetName(_search, "Поиск маркетплейсов по названию или пути");
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
        Grid.SetRow(_count, 3);
        root.Children.Add(_count);

        var listArea = new Grid();
        listArea.Children.Add(_list);
        listArea.Children.Add(_empty);
        Grid.SetRow(listArea, 4);
        root.Children.Add(listArea);
        Grid.SetRow(_status, 5);
        root.Children.Add(_status);

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var close = MakeButton("Закрыть");
        close.IsCancel = true;
        _remove.Background = Brush("#3B2426");
        _remove.Foreground = Brush("#F0A4A4");
        _remove.BorderBrush = Brush("#6A383B");
        _remove.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(_remove);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 6);
        root.Children.Add(buttons);
        return root;
    }

    private void RefreshList()
    {
        var selected = SelectedMarketplace();
        var query = _search.Text.Trim();
        _list.Items.Clear();
        var visibleCount = 0;
        foreach (var marketplace in _marketplaces
                     .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(item => item.Root, StringComparer.OrdinalIgnoreCase))
        {
            if (query.Length > 0
                && !marketplace.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !marketplace.Root.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            visibleCount++;
            var labels = new StackPanel();
            labels.Children.Add(new TextBlock
            {
                Text = marketplace.Name,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            labels.Children.Add(new TextBlock
            {
                Text = marketplace.Root,
                Foreground = Brush("#B0B5BE"),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = marketplace.Root,
                Margin = new Thickness(0, 3, 0, 0)
            });
            var listItem = new ListBoxItem
            {
                Tag = marketplace,
                Content = labels,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 5),
                Background = Brush("#1D1F23"),
                BorderBrush = Brush("#34373E"),
                BorderThickness = new Thickness(1)
            };
            _list.Items.Add(listItem);
            if (ReferenceEquals(marketplace, selected)) _list.SelectedItem = listItem;
        }
        _count.Text = query.Length == 0
            ? $"Маркетплейсов: {_marketplaces.Count}"
            : $"Найдено: {visibleCount} из {_marketplaces.Count}";
        _empty.Text = _marketplaces.Count == 0
            ? "Настроенных маркетплейсов нет."
            : "По вашему запросу ничего не найдено. Измените или очистите поиск.";
        _empty.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private async Task RemoveSelectedAsync()
    {
        if (_busy || SelectedMarketplace() is not { } marketplace) return;
        if (CodexMessageDialog.Show(this,
            $"Удалить маркетплейс «{marketplace.Name}» из профиля Codex? App Server также удалит локальный корень каталога, если он установлен.\n\n{marketplace.Root}",
            "Удалить маркетплейс", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _busy = true;
        _search.IsEnabled = false;
        _clearSearch.IsEnabled = false;
        _list.IsEnabled = false;
        _status.Text = "Удаление маркетплейса…";
        UpdateButtons();
        try
        {
            await _removeMarketplace(marketplace);
            _marketplaces.Remove(marketplace);
            _status.Text = $"Маркетплейс «{marketplace.Name}» удалён.";
            RefreshList();
        }
        catch (Exception error)
        {
            _status.Text = "Не удалось удалить маркетплейс: " + CodexCliService.CompactCommandMessage(error.Message, 600);
        }
        finally
        {
            _busy = false;
            _search.IsEnabled = true;
            _clearSearch.IsEnabled = _search.Text.Length > 0;
            _list.IsEnabled = true;
            UpdateButtons();
        }
    }

    private CodexMarketplace? SelectedMarketplace()
        => (_list.SelectedItem as ListBoxItem)?.Tag as CodexMarketplace;

    private void UpdateButtons() => _remove.IsEnabled = !_busy && SelectedMarketplace() is not null;

    private static Button MakeButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(13, 7, 13, 7),
        Background = Brush("#1D1F23"),
        Foreground = Brush("#F1F3F5"),
        BorderBrush = Brush("#34373E"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
