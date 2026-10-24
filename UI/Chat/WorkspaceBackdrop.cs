using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexGUI;

internal static class WorkspaceBackdrop
{
    private static readonly DrawingBrush Background = Load();

    internal static DrawingBrush Create() => Background;

    private static DrawingBrush Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexGUI.Workspace.Terrain.png")
            ?? throw new InvalidOperationException("Не найден встроенный фон рабочего пространства.");
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawImage(image, new Rect(0, 0, 1585, 992));
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(12, 0, 5, 8)), null, new Rect(0, 0, 1585, 992));

        }
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        brush.Freeze(); return brush;
    }
}
