using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace CodexGUI;

internal static class WorkspaceMapView
{
    internal static void Render(Canvas canvas, IReadOnlyList<WorkspaceMapNode> nodes, string root, double zoom, Action<string> select)
    {
        canvas.Children.Clear();
        canvas.Width = 330; canvas.Height = 230;
        canvas.RenderTransform = new ScaleTransform(zoom, zoom, 165, 115);
        canvas.ClipToBounds = true;
        for (var x = 6; x < 330; x += 15)
        for (var y = 6; y < 230; y += 15)
        {
            var dot = new Ellipse { Width = 1.4, Height = 1.4, Fill = Brush("#25434D"), IsHitTestVisible = false };
            Canvas.SetLeft(dot, x); Canvas.SetTop(dot, y); canvas.Children.Add(dot);
        }
        for (var i = 0; i < nodes.Count; i++)
        {
            var angle = i * Math.PI * 2 / nodes.Count - Math.PI / 2;
            var x = 165 + Math.Cos(angle) * 130;
            var y = 115 + Math.Sin(angle) * 84;
            canvas.Children.Add(new Line { X1 = 165, Y1 = 115, X2 = x, Y2 = y, Stroke = Brush("#3B847F"), StrokeThickness = 1 });
        }
        var colors = new[] { "#4D9CFF", "#56DBC2", "#9D86DC", "#CEA764", "#80969F", "#B16EE3" };
        for (var i = 0; i < nodes.Count; i++)
        {
            var angle = i * Math.PI * 2 / nodes.Count - Math.PI / 2;
            AddNode(165 + Math.Cos(angle) * 130, 115 + Math.Sin(angle) * 84, nodes[i].Label, nodes[i].Path, colors[i % colors.Length], false);
        }
        AddNode(165, 115, Path.GetFileName(root), root, "#70E8CC", true);
        if (nodes.Count == 0)
        {
            var empty = new TextBlock { Text = "Нет данных для этого вида", FontSize = 11, Foreground = Brush("#91A8B5") };
            Canvas.SetTop(empty, 200); Canvas.SetLeft(empty, 65); canvas.Children.Add(empty);
        }

        void AddNode(double x, double y, string label, string path, string color, bool center)
        {
            var content = new DockPanel();
            FrameworkElement icon = center ? new System.Windows.Shapes.Path
            {
                Width = 16, Height = 16, Stretch = Stretch.Uniform, Stroke = Brush(color), StrokeThickness = 1.8,
                Data = Geometry.Parse("M12,2 V22 M2,12 H22 M5,5 L19,19 M5,19 L19,5"),
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center
            } : new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(3), Background = Brush(color), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            Brush background = Brush("#15252D");
            if (center)
            {
                var centerFill = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                centerFill.GradientStops.Add(new GradientStop(Color.FromRgb(10, 46, 40), 0));
                centerFill.GradientStops.Add(new GradientStop(Color.FromRgb(9, 56, 46), 0.1));
                centerFill.GradientStops.Add(new GradientStop(Color.FromRgb(13, 54, 49), 0.3));
                centerFill.GradientStops.Add(new GradientStop(Color.FromRgb(9, 30, 31), 0.86));
                centerFill.GradientStops.Add(new GradientStop(Color.FromRgb(11, 43, 39), 1));
                background = centerFill;
            }
            var button = new Button { Content = content, Width = center ? 104 : Math.Clamp(label.Length * 6.5 + 34, 62, 100), Height = center ? 36 : 30, FontSize = center ? 12 : 11,
                Padding = new Thickness(7, 3, 7, 3), Background = background, Foreground = Brush("#D4EBED"),
                BorderBrush = Brush(color), BorderThickness = new Thickness(center ? 1.3 : 0.6), ToolTip = label + "\n" + path };
            if (center) button.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(80, 241, 205), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.28
            };
            button.Click += (_, _) => select(path);
            Canvas.SetLeft(button, x - button.Width / 2); Canvas.SetTop(button, y - button.Height / 2); canvas.Children.Add(button);
        }
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
