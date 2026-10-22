using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class CodexMarketplaceManagerWindow : Window
{
    private readonly IList<CodexMarketplace> _marketplaces;
    private readonly Func<CodexMarketplace, Task> _removeMarketplace;
    private readonly ListBox _list = new() { Background = Brush("#101820"), BorderBrush = Brush("#2A3946") };
    private readonly TextBlock _empty = new()
    {
        Text = "Настроенных маркетплейсов нет.",
        Foreground = Brush("#A7B5C0"),
        Margin = new Thickness(12)
    };
    private readonly TextBlock _status = new()
    {
        Foreground = Brush("#A7B5C0"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly Button _remove = new() { Content = "Удалить маркетплейс", IsEnabled = false };
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
        _remove.Click += async (_, _) => await RemoveSelectedAsync();
        RefreshList();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
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
            Foreground = Brush("#A7B5C0"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 12)
        };
        Grid.SetRow(note, 1);
        root.Children.Add(note);

        var listArea = new Grid();
        listArea.Children.Add(_list);
        listArea.Children.Add(_empty);
        Grid.SetRow(listArea, 2);
        root.Children.Add(listArea);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);

        var buttons = new StackPanel
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
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        return root;
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var marketplace in _marketplaces
                     .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(item => item.Root, StringComparer.OrdinalIgnoreCase))
        {
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
                Foreground = Brush("#A7B5C0"),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = marketplace.Root,
                Margin = new Thickness(0, 3, 0, 0)
            });
            _list.Items.Add(new ListBoxItem
            {
                Tag = marketplace,
                Content = labels,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 5),
                Background = Brush("#16212B"),
                BorderBrush = Brush("#2A3946"),
                BorderThickness = new Thickness(1)
            });
        }
        _empty.Visibility = _marketplaces.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private async Task RemoveSelectedAsync()
    {
        if (_busy || SelectedMarketplace() is not { } marketplace) return;
        if (CodexMessageDialog.Show(this,
            $"Удалить маркетплейс «{marketplace.Name}» из профиля Codex? App Server также удалит локальный корень каталога, если он установлен.\n\n{marketplace.Root}",
            "Удалить маркетплейс", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _busy = true;
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
            _status.Text = "Не удалось удалить маркетплейс: " + error.Message;
        }
        finally
        {
            _busy = false;
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
        Background = Brush("#16212B"),
        Foreground = Brush("#ECF2F5"),
        BorderBrush = Brush("#2A3946"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
