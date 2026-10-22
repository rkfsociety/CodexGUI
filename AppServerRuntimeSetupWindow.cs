using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class AppServerRuntimeSetupWindow : Window
{
    private readonly TextBlock _status;
    private readonly TextBlock _details;
    private readonly ProgressBar _progress;

    public AppServerRuntimeSetupWindow()
    {
        Title = "CodexGUI";
        Width = 460;
        Height = 178;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = new SolidColorBrush(Color.FromRgb(24, 33, 44));
        Foreground = new SolidColorBrush(Color.FromRgb(240, 240, 241));

        var layout = new StackPanel { Margin = new Thickness(22) };
        layout.Children.Add(new TextBlock
        {
            Text = "Подготавливаю Codex",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 9)
        });
        _status = new TextBlock { Text = $"Загружаю App Server {CodexAppServerRuntime.Version}…", FontSize = 12 };
        layout.Children.Add(_status);
        _progress = new ProgressBar
        {
            Height = 7,
            Margin = new Thickness(0, 13, 0, 7),
            IsIndeterminate = true,
            Foreground = new SolidColorBrush(Color.FromRgb(168, 230, 207)),
            Background = new SolidColorBrush(Color.FromRgb(43, 55, 70))
        };
        layout.Children.Add(_progress);
        _details = new TextBlock
        {
            Text = "Загружается закреплённый официальный runtime App Server. Терминальный интерфейс Codex CLI не запускается.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(164, 177, 194))
        };
        layout.Children.Add(_details);
        Content = layout;
    }

    public void Report(AppServerRuntimeProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => Report(progress));
            return;
        }

        _status.Text = progress.Status;
        if (progress.TotalBytes is { } total && total > 0)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = Math.Clamp(progress.BytesReceived * 100d / total, 0d, 100d);
            _details.Text = $"{(progress.BytesReceived / 1_048_576d).ToString("N1", CultureInfo.CurrentCulture)} из {(total / 1_048_576d).ToString("N1", CultureInfo.CurrentCulture)} МБ";
        }
        else
        {
            _progress.IsIndeterminate = true;
        }
    }
}
