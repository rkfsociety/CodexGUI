using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace CodexGUI;

internal static class CodexWindowTheme
{
    public static void Apply(Window window)
    {
        window.SetResourceReference(Control.BackgroundProperty, "Canvas");
        window.SetResourceReference(Control.ForegroundProperty, "TextMain");
        window.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        window.FontSize = 13;
        window.SourceInitialized += (_, _) => ApplyDarkTitleBar(window);
    }

    public static void ApplyDarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        uint captionColor = 0x00141110;
        uint textColor = 0x00F1F0F0;
        _ = DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(uint));
        _ = DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(uint));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int valueSize);
}
