using System;
using System.Windows;
using System.Windows.Media;

namespace CodexGUI;

internal static class WorkspaceBackdrop
{
    internal static DrawingBrush Create()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(5, 13, 17)), null, new Rect(0, 0, 1000, 800));
            var dot = new SolidColorBrush(Color.FromArgb(120, 42, 88, 94));
            for (var x = 12; x < 1000; x += 23)
            for (var y = 12; y < 800; y += 23)
                context.DrawEllipse(dot, null, new Point(x, y), 0.7, 0.7);
            context.DrawEllipse(new RadialGradientBrush(Color.FromArgb(40, 47, 201, 173), Colors.Transparent), null, new Point(500, 370), 390, 330);
            // Layered contour lines provide the reference's quiet terrain without a raster UI background.
            for (var row = 0; row < 38; row++)
            {
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    for (var x = 0; x <= 1000; x += 8)
                    {
                        var y = 635 + row * 5 + Math.Sin(x * 0.011 + row * 0.21) * (24 + row * 0.6)
                            + Math.Cos(x * 0.025 - row * 0.15) * 10 - Math.Pow((x - 500) / 500d, 2) * 65;
                        if (x == 0) path.BeginFigure(new Point(x, y), false, false);
                        else path.LineTo(new Point(x, y), true, false);
                    }
                }
                context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(80, 30, 75, 91)), 0.8), geometry);
            }
        }
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        brush.Freeze(); return brush;
    }
}
