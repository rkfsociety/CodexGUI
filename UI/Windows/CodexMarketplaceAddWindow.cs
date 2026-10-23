using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed record CodexMarketplaceAddRequest(string Source, string? RefName, IReadOnlyList<string> SparsePaths);

internal sealed class CodexMarketplaceAddWindow : Window
{
    private readonly TextBox _source = new();
    private readonly TextBox _refName = new();
    private readonly TextBox _sparsePaths = new();

    public CodexMarketplaceAddRequest? Result { get; private set; }

    public CodexMarketplaceAddWindow()
    {
        Title = "Добавить маркетплейс";
        Width = 560;
        Height = 520;
        MinWidth = 500;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
        Loaded += (_, _) => _source.Focus();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "Добавить маркетплейс",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });

        var form = new StackPanel { Margin = new Thickness(0, 16, 0, 12) };
        AddLabel(form, "ИСТОЧНИК (например, owner/repo или Git URL)");
        StyleTextBox(_source, 38);
        _source.MaxLength = 2048;
        form.Children.Add(_source);

        AddLabel(form, "ВЕТКА ИЛИ TAG (необязательно)", new Thickness(0, 14, 0, 5));
        StyleTextBox(_refName, 36);
        _refName.MaxLength = 256;
        form.Children.Add(_refName);

        AddLabel(form, "SPARSE PATHS (необязательно, по одному на строку)", new Thickness(0, 14, 0, 5));
        _sparsePaths.AcceptsReturn = true;
        _sparsePaths.TextWrapping = TextWrapping.Wrap;
        _sparsePaths.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _sparsePaths.MinHeight = 90;
        _sparsePaths.MaxLength = 4096;
        _sparsePaths.Padding = new Thickness(10, 8, 10, 8);
        _sparsePaths.Background = Brush("#181A1E");
        _sparsePaths.Foreground = Brush("#F1F3F5");
        _sparsePaths.BorderBrush = Brush("#314454");
        _sparsePaths.BorderThickness = new Thickness(1);
        form.Children.Add(_sparsePaths);
        form.Children.Add(new TextBlock
        {
            Text = "Источник будет сохранён в общем профиле Codex. После добавления список доступных плагинов обновится.",
            Foreground = Brush("#B0B5BE"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = MakeButton("Отмена");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var add = MakeButton("Добавить");
        add.IsDefault = true;
        add.Background = Brush("#204139");
        add.Foreground = Brush("#92E5C7");
        add.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        return root;
    }

    private void Save()
    {
        var source = _source.Text.Trim();
        if (source.Length == 0)
        {
            CodexMessageDialog.Show(this, "Укажите источник маркетплейса.", "Проверьте источник",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var refName = string.IsNullOrWhiteSpace(_refName.Text) ? null : _refName.Text.Trim();
        var sparsePaths = _sparsePaths.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim())
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Result = new CodexMarketplaceAddRequest(source, refName, sparsePaths);
        DialogResult = true;
    }

    private static void AddLabel(Panel parent, string text, Thickness? margin = null)
        => parent.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#8D95A2"),
            Margin = margin ?? new Thickness(0, 0, 0, 5)
        });

    private static void StyleTextBox(TextBox textBox, double height)
    {
        textBox.Height = height;
        textBox.Padding = new Thickness(10, 7, 10, 5);
        textBox.Background = Brush("#181A1E");
        textBox.Foreground = Brush("#F1F3F5");
        textBox.BorderBrush = Brush("#314454");
        textBox.BorderThickness = new Thickness(1);
    }

    private static Button MakeButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(13, 7, 13, 7),
        Background = Brush("#1D1F23"),
        Foreground = Brush("#F1F3F5"),
        BorderBrush = Brush("#34373E"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
