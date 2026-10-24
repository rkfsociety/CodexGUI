using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

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
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            RenderTransform = new TranslateTransform(6, 1),
            Foreground = Brush("#F1F3F5"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 37,
            TextAlignment = TextAlignment.Center
        });
        welcome.Children.Add(new TextBlock
        {
            Text = detail,
            RenderTransform = new TranslateTransform(6, 0),
            FontSize = 17,
            LineHeight = 24,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#A4B1C2"),
            Margin = new Thickness(0, 2, 0, 27),
            MaxWidth = 660,
            TextAlignment = TextAlignment.Center
        });

        if (suggestions.Count > 0)
        {
            var rows = new StackPanel { Width = 530 };
            var widths = new[] { 274d, 238d, 304d, 214d };
            StackPanel? row = null;
            for (var index = 0; index < suggestions.Count; index++)
            {
                if (index % 2 == 0)
                {
                    row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                    rows.Children.Add(row);
                }
                var gap = index % 2 == 0 ? (index % 4 == 0 ? 14 : 12) : 0;
                row!.Children.Add(CreateSuggestion(suggestions[index], onSuggestion, widths[index % widths.Length], gap));
            }
            welcome.Children.Add(new Viewbox
            {
                Child = rows, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10, 1, 0, 0)
            });
        }

        return welcome;
    }

    private static Button CreateSuggestion(string text, Action<string>? onSuggestion, double width, double gap)
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
            Margin = new Thickness(0, 6, gap, 6),
            Width = width,
            Height = 44,
            BorderBrush = Brush("#29454E"),
            BorderThickness = new Thickness(1)
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onSuggestion?.Invoke(text);
        return button;
    }

    private static FrameworkElement CreateProjectOrbit(Action<string>? onSuggestion)
    {
        var canvas = new Canvas { Width = 700, Height = 402, ClipToBounds = false };
        var glow = new Ellipse
        {
            Width = 360, Height = 330,
            Fill = new RadialGradientBrush(Color.FromArgb(68, 40, 217, 179), Colors.Transparent),
            IsHitTestVisible = false
        };
        Place(glow, 170, 60);
        foreach (var (angle, width, height) in new[] { (-24d, 440d, 160d), (24d, 440d, 160d), (-3d, 470d, 228d) })
        {
            var orbit = new Ellipse
            {
                Width = width, Height = height, Stroke = Brush("#315C62"), StrokeThickness = 0.65, Opacity = 0.75,
                RenderTransform = new RotateTransform(angle, width / 2, height / 2), IsHitTestVisible = false
            };
            Place(orbit, 350 - width / 2, 218 - height / 2);
        }
        AddCurve("M228,86 C334,80 350,166 350,218 C354,285 434,278 480,296");
        AddCurve("M480,94 C411,106 383,155 350,218 C297,227 240,250 210,284");
        var plate = Glass(152, 146, 23, "#383B8C82", "#4674CABC");
        plate.RenderTransform = new SkewTransform(0, 3, 76, 73);
        Place(plate, 278, 143);
        var rear = Glass(94, 98, 24, "#3264F7CD", "#786EF3D2");
        rear.Effect = new DropShadowEffect { Color = Color.FromRgb(77, 255, 207), BlurRadius = 30, ShadowDepth = 0, Opacity = 0.45 };
        Place(rear, 310, 156);
        var cube = Glass(98, 100, 24, "#8654DFC0", "#A482F8D8");
        Place(cube, 302, 167);
        var face = new Border
        {
            Width = 74, Height = 74, CornerRadius = new CornerRadius(18), BorderBrush = Brush("#A3FBE0"), BorderThickness = new Thickness(2),
            Background = new LinearGradientBrush(Color.FromRgb(25, 79, 73), Color.FromRgb(5, 26, 31), 100),
            Effect = new DropShadowEffect { Color = Color.FromRgb(88, 245, 200), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.6 },
            Child = new Path { Data = Geometry.Parse("M24,3 V45 M3,24 H45 M9,9 L39,39 M9,39 L39,9"),
                Stroke = Brush("#9AF4D9"), StrokeThickness = 3.5, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, Width = 48, Height = 48, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        Place(face, 315, 180);
        AddCard(54, 48, -10, "folder", "Понимаю\nваш проект", "Анализирую файлы\nи зависимости...", "Объясни архитектуру проекта");
        AddCard(480, 48, 10, "code", "Пишу код", "Создаю, редактирую\nи объясняю решение", "Помоги реализовать новую функциональность");
        AddCard(40, 282, 3, "document", "Отвечаю\nна вопросы", "По коду, архитектуре\nи технологиям", "Объясни, как работает этот проект");
        AddCard(480, 296, 6, "idea", "Предлагаю\nулучшения", "Нахожу проблемы\nи варианты развития", "Предложи улучшения проекта");
        foreach (var (x, y, size, color) in new[]
        {
            (228d, 86d, 5d, "#A5E5DC"), (230d, 144d, 9d, "#7EF1D7"), (480d, 94d, 5d, "#A5E5DC"),
            (480d, 137d, 8d, "#7EF1D7"), (210d, 284d, 8d, "#7EF1D7"), (476d, 358d, 6d, "#F4CE74"),
            (522d, 290d, 6d, "#E0E9DE"), (424d, 231d, 3d, "#B1F7E9")
        }) Place(new Ellipse { Width = size, Height = size, Fill = Brush(color), IsHitTestVisible = false }, x - size / 2, y - size / 2);
        return new Viewbox { Child = canvas, Stretch = Stretch.Uniform, MaxWidth = 700, Margin = new Thickness(0, 0, 0, 18) };

        void Place(FrameworkElement element, double x, double y)
        {
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element);
        }
        void AddCurve(string data) => canvas.Children.Add(new Path
        {
            Data = Geometry.Parse(data), Stroke = Brush("#3D7479"), StrokeThickness = 0.7, Opacity = 0.65, IsHitTestVisible = false
        });
        Border Glass(double width, double height, double radius, string fill, string stroke) => new()
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(radius), Background = Brush(fill),
            BorderBrush = Brush(stroke), BorderThickness = new Thickness(0.8), IsHitTestVisible = false
        };
        void AddCard(double x, double y, double angle, string icon, string heading, string detail, string prompt)
        {
            var content = new Grid { Margin = new Thickness(16, 16, 0, 12) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(43) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            var glyph = new Path
            {
                Width = 24, Height = 24, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 8, 0),
                Data = Geometry.Parse(icon switch
                {
                    "folder" => "M2,6 H10 L12,9 H22 V21 H2 Z M2,6 V3 H9 L12,6 H20 V9",
                    "code" => "M7,5 L1,12 L7,19 M17,5 L23,12 L17,19 M14,2 L10,22",
                    "document" => "M4,2 H20 V22 H4 Z M8,7 H11 M14,7 H16 M8,12 H16 M8,17 H14",
                    _ => "M8,18 H16 M9,21 H15 M8,15 C2,10 5,3 12,3 C19,3 22,10 16,15 V17 H8 Z M12,0 V1 M2,3 L4,5 M22,3 L20,5 M0,11 H2 M22,11 H24"
                }),
                Stroke = Brush(icon == "idea" ? "#F2C45E" : "#6BF1D0"), StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Effect = new DropShadowEffect { Color = (Color)ColorConverter.ConvertFromString(icon == "idea" ? "#F2C45E" : "#6BF1D0"), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.4 }
            };
            content.Children.Add(glyph);
            var text = new StackPanel(); Grid.SetColumn(text, 1);
            text.Children.Add(new TextBlock { Text = heading, FontSize = 14, LineHeight = 20, Foreground = Brush("#D0E1EE"), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 11, LineHeight = 16.5, Foreground = Brush("#94ADBD"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
            content.Children.Add(text);
            var chrome = new Border
            {
                CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0.8), BorderBrush = Brush(icon == "idea" ? "#645A3C" : "#30525F"),
                Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#E912252E"), (Color)ColorConverter.ConvertFromString("#F00B171D"), 100),
                Child = content
            };
            var width = icon == "folder" ? 180 : icon == "document" ? 190 : 185;
            var card = new Button { Content = chrome, Width = width, Height = 120, Padding = new Thickness(0),
                Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch, ToolTip = prompt,
                RenderTransform = new RotateTransform(angle, width / 2, 60) };
            AutomationProperties.SetName(card, heading.Replace('\n', ' '));
            card.Click += (_, _) => onSuggestion?.Invoke(prompt);
            Place(card, x, y);
        }
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
