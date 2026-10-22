using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal static class ChatWelcomeView
{
    internal static StackPanel Create(
        string title,
        string detail,
        IReadOnlyList<string> suggestions,
        Action<string>? onSuggestion)
    {
        var welcome = new StackPanel
        {
            Margin = new Thickness(0, 32, 0, 0),
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        welcome.Children.Add(new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(14),
            Background = Brush("#223B38"),
            BorderBrush = Brush("#2A3946"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 16),
            Child = new TextBlock
            {
                Text = "✳",
                FontSize = 25,
                Foreground = Brush("#92E5C7"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        welcome.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 32,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#ECF2F5"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 37,
            TextAlignment = TextAlignment.Center
        });
        welcome.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 15,
            LineHeight = 24,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#A4B1C2"),
            Margin = new Thickness(0, 10, 0, 18),
            MaxWidth = 660,
            TextAlignment = TextAlignment.Center
        });

        if (suggestions.Count > 0)
        {
            var suggestionRow = new WrapPanel
            {
                MaxWidth = 820,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            foreach (var text in suggestions)
                suggestionRow.Children.Add(CreateSuggestion(text, onSuggestion));
            welcome.Children.Add(suggestionRow);
        }

        return welcome;
    }

    private static Button CreateSuggestion(string text, Action<string>? onSuggestion)
    {
        var button = new Button
        {
            Content = new Grid
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = text,
                        Margin = new Thickness(0, 0, 24, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "↗",
                        Foreground = Brush("#777981"),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brush("#16212B"),
            Foreground = Brush("#D1D2D5"),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 4, 9, 4),
            Width = 258,
            BorderBrush = Brush("#2A3946"),
            BorderThickness = new Thickness(1)
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onSuggestion?.Invoke(text);
        return button;
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
