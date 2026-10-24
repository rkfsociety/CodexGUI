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
        canvas.RenderTransform = new ScaleTransform(zoom, zoom, 150, 115);
        canvas.ClipToBounds = true;
        for (var x = 6; x < 300; x += 15)
        for (var y = 6; y < 230; y += 15)
        {
            var dot = new Ellipse { Width = 1.4, Height = 1.4, Fill = Brush("#25434D"), IsHitTestVisible = false };
            Canvas.SetLeft(dot, x); Canvas.SetTop(dot, y); canvas.Children.Add(dot);
        }
        for (var i = 0; i < nodes.Count; i++)
        {
            var angle = i * Math.PI * 2 / nodes.Count - Math.PI / 2;
            var x = 150 + Math.Cos(angle) * 100;
            var y = 115 + Math.Sin(angle) * 80;
            canvas.Children.Add(new Line { X1 = 150, Y1 = 115, X2 = x, Y2 = y, Stroke = Brush("#3B847F"), StrokeThickness = 1 });
        }
        var colors = new[] { "#4D9CFF", "#56DBC2", "#9D86DC", "#CEA764", "#80969F", "#B16EE3" };
        for (var i = 0; i < nodes.Count; i++)
        {
            var angle = i * Math.PI * 2 / nodes.Count - Math.PI / 2;
            AddNode(150 + Math.Cos(angle) * 100, 115 + Math.Sin(angle) * 80, nodes[i].Label, nodes[i].Path, colors[i % colors.Length], false);
        }
        AddNode(150, 115, Path.GetFileName(root), root, "#70E8CC", true);
        if (nodes.Count == 0)
        {
            var empty = new TextBlock { Text = "Нет данных для этого вида", FontSize = 10, Foreground = Brush("#91A8B5") };
            Canvas.SetTop(empty, 200); Canvas.SetLeft(empty, 65); canvas.Children.Add(empty);
        }

        void AddNode(double x, double y, string label, string path, string color, bool center)
        {
            var content = new DockPanel();
            var icon = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = Brush(color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, Width = center ? 96 : 88, Height = center ? 36 : 30, FontSize = 10,
                Padding = new Thickness(7, 3, 7, 3), Background = Brush(center ? "#104B46" : "#15252D"), Foreground = Brush("#D4EBED"),
                BorderBrush = Brush(color), BorderThickness = new Thickness(center ? 1.3 : 0.6), ToolTip = label + "\n" + path };
            button.Click += (_, _) => select(path);
            Canvas.SetLeft(button, x - button.Width / 2); Canvas.SetTop(button, y - button.Height / 2); canvas.Children.Add(button);
        }
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
