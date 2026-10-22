using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class CodexAppServerLoginWindow : Window
{
    private readonly TaskCompletionSource<bool> _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _verificationUrl;
    private readonly string _userCode;
    private readonly TextBlock _status;

    public CodexAppServerLoginWindow(string verificationUrl, string userCode)
    {
        _verificationUrl = verificationUrl;
        _userCode = userCode;
        Title = "Вход в Codex";
        Width = 440;
        Height = 250;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = Color("#18212C");
        Foreground = Color("#F0F0F1");

        var layout = new StackPanel { Margin = new Thickness(22) };
        _status = new TextBlock
        {
            Text = "Ожидаю подтверждения…",
            FontSize = 10,
            Foreground = Color("#A4B1C2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        layout.Children.Add(new TextBlock
        {
            Text = "Подключите аккаунт ChatGPT",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        layout.Children.Add(new TextBlock
        {
            Text = "Откройте страницу авторизации и введите код:",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
        var code = new TextBox
        {
            Text = userCode,
            IsReadOnly = true,
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 12, 0, 10),
            Background = Color("#222E3D"),
            BorderThickness = new Thickness(0),
            SelectionBrush = Color("#3D655E")
        };
        code.GotKeyboardFocus += (_, _) => code.SelectAll();
        layout.Children.Add(code);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = "Скопировать код", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_userCode);
                _status.Text = "Код скопирован. Подтвердите вход в браузере.";
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            {
                _status.Text = "Не удалось скопировать код: " + error.Message;
            }
        };
        var open = new Button
        {
            Content = "Открыть страницу",
            Padding = new Thickness(10, 6, 10, 6),
            Background = Color("#2B5A4A"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        open.Click += (_, _) => OpenVerificationPage();
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(10, 6, 10, 6), Background = Color("#4A2A31") };
        cancel.Click += (_, _) => Cancel();
        buttons.Children.Add(copy);
        buttons.Children.Add(open);
        buttons.Children.Add(cancel);
        layout.Children.Add(buttons);

        layout.Children.Add(_status);
        Content = layout;
        Closed += (_, _) => _cancelled.TrySetResult(true);
    }

    public Task Cancelled => _cancelled.Task;

    private void Cancel()
    {
        _cancelled.TrySetResult(true);
        Close();
    }

    private void OpenVerificationPage()
    {
        if (!Uri.TryCreate(_verificationUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("auth.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "Сервер вернул недопустимый адрес страницы входа.";
            return;
        }

        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "Не удалось открыть браузер: " + error.Message;
        }
    }

    private static SolidColorBrush Color(string value) => new((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));
}
