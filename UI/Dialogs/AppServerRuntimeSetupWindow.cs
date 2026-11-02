using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
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
        Height = 210;
        MinWidth = 400;
        MinHeight = 190;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        CodexWindowTheme.Apply(this);

        var layout = new StackPanel { Margin = new Thickness(22) };
        layout.Children.Add(new TextBlock
        {
            Text = "Подготавливаю Codex",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 9)
        });
        _status = new TextBlock
        {
            Text = $"Загружаю App Server {CodexAppServerRuntime.Version}…",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(_status, "Состояние подготовки App Server");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        layout.Children.Add(_status);
        _progress = new ProgressBar
        {
            Height = 7,
            Margin = new Thickness(0, 13, 0, 7),
            IsIndeterminate = true,
            Foreground = new SolidColorBrush(Color.FromRgb(168, 230, 207)),
            Background = new SolidColorBrush(Color.FromRgb(43, 55, 70))
        };
        System.Windows.Automation.AutomationProperties.SetName(_progress, "Загрузка App Server");
        layout.Children.Add(_progress);
        _details = new TextBlock
        {
            Text = "Загружается закреплённый официальный runtime App Server. Терминальный интерфейс Codex CLI не запускается.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(164, 177, 194))
        };
        AutomationProperties.SetName(_details, "Подробности загрузки App Server");
        layout.Children.Add(_details);
        Content = new ScrollViewer
        {
            Content = layout,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
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
            var received = Math.Clamp(progress.BytesReceived, 0L, total);
            _progress.Value = received * 100d / total;
            _details.Text = $"{_progress.Value.ToString("N0", CultureInfo.CurrentCulture)} % · " +
                $"{(received / 1_048_576d).ToString("N1", CultureInfo.CurrentCulture)} из {(total / 1_048_576d).ToString("N1", CultureInfo.CurrentCulture)} МБ";
        }
        else
        {
            _progress.IsIndeterminate = true;
            _details.Text = progress.BytesReceived > 0
                ? $"Загружено {(progress.BytesReceived / 1_048_576d).ToString("N1", CultureInfo.CurrentCulture)} МБ. Общий размер пока неизвестен."
                : "Подготовка App Server продолжается. Ожидайте завершения…";
        }
    }
}
