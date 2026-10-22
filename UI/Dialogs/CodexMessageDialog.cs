using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexGUI;

internal static class CodexMessageDialog
{
    public static MessageBoxResult Show(Window owner, string message, string caption,
        MessageBoxButton buttons, MessageBoxImage image)
        => ShowCore(owner, message, caption, buttons, image);

    public static MessageBoxResult Show(string message, string caption,
        MessageBoxButton buttons, MessageBoxImage image)
        => ShowCore(null, message, caption, buttons, image);

    private static MessageBoxResult ShowCore(Window? owner, string message, string caption,
        MessageBoxButton buttons, MessageBoxImage image)
    {
        var window = new Window
        {
            Title = caption,
            Width = 470,
            MinWidth = 390,
            MaxWidth = 560,
            MinHeight = 170,
            MaxHeight = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            WindowStyle = WindowStyle.SingleBorderWindow
        };
        CodexWindowTheme.Apply(window);

        var options = GetActions(buttons);
        var (glyph, accent) = GetIcon(image);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 14)
        };
        heading.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = Brush("#24" + accent[1..]),
            Child = new TextBlock
            {
                Text = glyph,
                Foreground = Brush(accent),
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        });
        heading.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 19,
            Margin = new Thickness(41, 0, 2, 0)
        };
        var messageScroll = new ScrollViewer
        {
            MaxHeight = 330,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = messageBlock
        };
        Grid.SetRow(messageScroll, 1);
        root.Children.Add(messageScroll);

        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        MessageBoxResult selectedResult = MessageBoxResult.None;
        foreach (var action in options.AsEnumerable().Reverse())
        {
            var button = new Button
            {
                Content = action.Label,
                MinWidth = 82,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(13, 7, 13, 7),
                IsDefault = action.IsDefault,
                Background = action.IsDefault ? Brush("#204139") : Brush("#16212B"),
                Foreground = action.IsDefault ? Brush("#92E5C7") : Brush("#ECF2F5"),
                BorderBrush = action.IsDefault ? Brush("#365C53") : Brush("#2A3946"),
                BorderThickness = new Thickness(1)
            };
            button.Click += (_, _) =>
            {
                selectedResult = action.Result;
                window.DialogResult = true;
            };
            actionPanel.Children.Add(button);
        }
        Grid.SetRow(actionPanel, 2);
        root.Children.Add(actionPanel);

        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            selectedResult = options.FirstOrDefault(action => action.IsCancel)?.Result ?? MessageBoxResult.None;
            window.DialogResult = true;
            e.Handled = true;
        };
        window.Content = new Border
        {
            Background = Brush("#16212B"),
            BorderBrush = Brush("#2A3946"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(10),
            Child = root
        };
        window.ShowDialog();
        return selectedResult;
    }

    private static IReadOnlyList<DialogAction> GetActions(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OK => [new("Понятно", MessageBoxResult.OK, true, true)],
        MessageBoxButton.OKCancel =>
        [
            new("ОК", MessageBoxResult.OK, true, false),
            new("Отмена", MessageBoxResult.Cancel, false, true)
        ],
        MessageBoxButton.YesNo =>
        [
            new("Да", MessageBoxResult.Yes, true, false),
            new("Нет", MessageBoxResult.No, false, true)
        ],
        MessageBoxButton.YesNoCancel =>
        [
            new("Да", MessageBoxResult.Yes, true, false),
            new("Нет", MessageBoxResult.No, false, false),
            new("Отмена", MessageBoxResult.Cancel, false, true)
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(buttons))
    };

    private static (string Glyph, string Accent) GetIcon(MessageBoxImage image)
    {
        if (image == MessageBoxImage.Error)
            return ("!", "#F28B82");
        if (image == MessageBoxImage.Warning)
            return ("!", "#E6BC70");
        if (image == MessageBoxImage.Question) return ("?", "#92E5C7");
        if (image == MessageBoxImage.Information)
            return ("i", "#92E5C7");
        return ("·", "#92E5C7");
    }

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));

    private sealed record DialogAction(string Label, MessageBoxResult Result, bool IsDefault, bool IsCancel);
}
