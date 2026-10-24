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
            Margin = new Thickness(0, 48, 0, 0),
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (suggestions.Count > 0)
            welcome.Children.Add(CreateProjectOrbit(onSuggestion));
        else welcome.Children.Add(new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(14),
            Background = Brush("#223B38"),
            BorderBrush = Brush("#34373E"),
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
            Foreground = Brush("#F1F3F5"),
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
                MaxWidth = 570,
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
            Background = Brush("#101D23"),
            Foreground = Brush("#D1D2D5"),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 4, 9, 4),
            Width = 258,
            BorderBrush = Brush("#29454E"),
            BorderThickness = new Thickness(1)
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onSuggestion?.Invoke(text);
        return button;
    }

    private static FrameworkElement CreateProjectOrbit(Action<string>? onSuggestion)
    {
        var canvas = new Canvas { Width = 620, Height = 370, ClipToBounds = true };
        for (var x = 10; x < 620; x += 22)
        for (var y = 10; y < 370; y += 22)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 1.5, Height = 1.5, Fill = Brush("#24434A"), IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, x); Canvas.SetTop(dot, y); canvas.Children.Add(dot);
        }
        var glow = new System.Windows.Shapes.Ellipse
        {
            Width = 350, Height = 320,
            Fill = new RadialGradientBrush(Color.FromArgb(95, 44, 222, 186), Colors.Transparent),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(glow, 135); Canvas.SetTop(glow, 20); canvas.Children.Add(glow);
        foreach (var angle in new[] { -25d, 25d })
        {
            var orbit = new System.Windows.Shapes.Ellipse
            {
                Width = 380, Height = 150, Stroke = Brush("#315F66"), StrokeThickness = 0.8,
                RenderTransform = new RotateTransform(angle, 190, 75), IsHitTestVisible = false
            };
            Canvas.SetLeft(orbit, 120); Canvas.SetTop(orbit, 110); canvas.Children.Add(orbit);
        }
        var emblem = new Border
        {
            Width = 100, Height = 100, CornerRadius = new CornerRadius(24),
            BorderBrush = Brush("#70E8CC"), BorderThickness = new Thickness(1.5),
            Background = new LinearGradientBrush(Color.FromRgb(26, 115, 103), Color.FromRgb(9, 30, 35), 90),
            Child = new TextBlock { Text = "✳", FontSize = 66, Foreground = Brush("#82F5D9"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        Canvas.SetLeft(emblem, 260); Canvas.SetTop(emblem, 135); canvas.Children.Add(emblem);
        AddCard(8, 22, "▱", "Понимаю\nваш проект", "Анализирую файлы и связи", "Объясни архитектуру проекта");
        AddCard(420, 22, "‹/›", "Пишу код", "Создаю и редактирую", "Помоги реализовать новую функциональность");
        AddCard(8, 225, "▤", "Отвечаю\nна вопросы", "По коду и технологиям", "Объясни, как работает этот проект");
        AddCard(420, 225, "✧", "Предлагаю\nулучшения", "Нахожу проблемы и решения", "Предложи улучшения проекта");
        return new Viewbox { Child = canvas, Stretch = Stretch.Uniform, MaxWidth = 660, Margin = new Thickness(0, 0, 0, 18) };

        void AddCard(double x, double y, string icon, string heading, string detail, string prompt)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = icon + "  " + heading, FontSize = 14, Foreground = Brush("#BFF7EB"), FontWeight = FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#99B2BF"), Margin = new Thickness(0, 10, 0, 0) });
            var card = new Button { Content = content, Width = 185, Height = 125, Padding = new Thickness(12),
                Background = Brush("#0D1C23"), BorderBrush = Brush("#31535F"), BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = prompt };
            AutomationProperties.SetName(card, heading);
            card.Click += (_, _) => onSuggestion?.Invoke(prompt);
            card.RenderTransform = new RotateTransform(x < 300 ? -9 : 9, 92, 62);
            Canvas.SetLeft(card, x); Canvas.SetTop(card, y); canvas.Children.Add(card);
        }
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
