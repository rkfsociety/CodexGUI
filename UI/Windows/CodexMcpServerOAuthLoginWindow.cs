using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class CodexMcpServerOAuthLoginWindow : Window
{
    private readonly Uri _authorizationUri;
    private readonly TaskCompletionSource<bool> _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _status = new()
    {
        Foreground = Brush("#A7B5C0"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 12, 0, 0)
    };

    public CodexMcpServerOAuthLoginWindow(string serverName, string authorizationUrl)
    {
        if (!Uri.TryCreate(authorizationUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("MCP-сервер вернул недопустимый адрес входа.");

        _authorizationUri = uri;
        Title = $"Вход в MCP-сервер «{serverName}»";
        Width = 520;
        Height = 300;
        MinWidth = 440;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout(serverName);
        Closed += (_, _) => _cancelled.TrySetResult(true);
    }

    public Task Cancelled => _cancelled.Task;

    private UIElement BuildLayout(string serverName)
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "Подключение MCP-сервера",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        });
        var description = new TextBlock
        {
            Text = $"Чтобы завершить вход в «{serverName}», откройте страницу авторизации. Проверьте домен перед продолжением.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(description, 1);
        root.Children.Add(description);
        var host = new TextBlock
        {
            Text = _authorizationUri.GetLeftPart(UriPartial.Authority),
            Foreground = Brush("#92E5C7"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 9, 0, 0)
        };
        Grid.SetRow(host, 2);
        root.Children.Add(host);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var open = MakeButton("Открыть страницу входа", accent: true);
        open.Click += (_, _) => OpenAuthorizationPage();
        var cancel = MakeButton("Отмена");
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(open);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        return root;
    }

    private void OpenAuthorizationPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_authorizationUri.AbsoluteUri) { UseShellExecute = true });
            _status.Text = "Завершите вход в браузере. Окно закроется после подтверждения сервером.";
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "Не удалось открыть браузер: " + error.Message;
        }
    }

    private static Button MakeButton(string text, bool accent = false) => new()
    {
        Content = text,
        Padding = new Thickness(12, 7, 12, 7),
        Background = Brush(accent ? "#223B38" : "#16212B"),
        Foreground = Brush(accent ? "#92E5C7" : "#ECF2F5"),
        BorderBrush = Brush("#2A3946"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
