using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexGUI;

internal sealed class CodexWorktreeSetupWindow : Window
{
    private const int MaximumOutputCharacters = 120_000;
    private readonly string _worktreePath;
    private readonly string _scriptPath;
    private readonly TextBlock _status = new();
    private readonly TextBox _output = new();
    private readonly Button _cancelButton = new() { Content = "Остановить" };
    private Process? _process;
    private bool _cancelRequested;

    public bool Succeeded { get; private set; }
    public string? FailureMessage { get; private set; }

    private CodexWorktreeSetupWindow(Window? owner, string worktreePath, string scriptPath)
    {
        if (owner is not null) Owner = owner;
        _worktreePath = worktreePath;
        _scriptPath = scriptPath;
        Title = "Подготовка рабочей копии";
        Width = 700;
        Height = 470;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CodexWindowTheme.Apply(this);
        Content = BuildLayout();
        Loaded += async (_, _) => await RunSetupAsync();
        _cancelButton.Click += (_, _) => CancelOrClose();
        Closing += (_, e) =>
        {
            if (_process is { HasExited: false })
            {
                e.Cancel = true;
                CancelProcess();
            }
        };
    }

    public static bool Run(Window owner, string worktreePath, string scriptPath)
    {
        var window = new CodexWorktreeSetupWindow(owner, worktreePath, scriptPath);
        window.ShowDialog();
        if (!window.Succeeded)
            throw new InvalidOperationException(window.FailureMessage ?? "Подготовка worktree не завершилась.");
        return true;
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "Подготовка рабочей копии",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        });

        var details = new StackPanel { Margin = new Thickness(0, 8, 0, 12) };
        details.Children.Add(new TextBlock
        {
            Text = $"Сценарий: {_scriptPath}",
            Foreground = Brush("#A7B5C0"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = _scriptPath
        });
        details.Children.Add(new TextBlock
        {
            Text = $"Папка: {_worktreePath}",
            Foreground = Brush("#8494A2"),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = _worktreePath,
            Margin = new Thickness(0, 4, 0, 0)
        });
        Grid.SetRow(details, 1);
        root.Children.Add(details);

        var outputBorder = new Border
        {
            Background = Brush("#0D151D"),
            BorderBrush = Brush("#2A3946"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10)
        };
        _output.IsReadOnly = true;
        _output.AcceptsReturn = true;
        _output.TextWrapping = TextWrapping.Wrap;
        _output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _output.Background = Brushes.Transparent;
        _output.Foreground = Brush("#D5E1E8");
        _output.BorderThickness = new Thickness(0);
        _output.FontFamily = new FontFamily("Consolas");
        _output.FontSize = 11;
        outputBorder.Child = _output;
        Grid.SetRow(outputBorder, 2);
        root.Children.Add(outputBorder);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        _status.Text = "Сценарий выполняется с правами текущего пользователя.";
        _status.Foreground = Brush("#A7B5C0");
        _status.FontSize = 10;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(_status, Dock.Left);
        footer.Children.Add(_status);
        _cancelButton.Padding = new Thickness(12, 7, 12, 7);
        _cancelButton.Background = Brush("#3B2529");
        _cancelButton.Foreground = Brush("#F2C5C8");
        _cancelButton.BorderBrush = Brush("#654047");
        _cancelButton.BorderThickness = new Thickness(1);
        _cancelButton.Margin = new Thickness(12, 0, 0, 0);
        _cancelButton.MinWidth = 88;
        DockPanel.SetDock(_cancelButton, Dock.Right);
        footer.Children.Add(_cancelButton);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private async Task RunSetupAsync()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = _worktreePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(_scriptPath);

        try
        {
            _process = process;
            if (!process.Start()) throw new InvalidOperationException("Не удалось запустить powershell.exe.");
            _status.Text = "Сценарий выполняется…";
            var outputTask = ReadOutputAsync(process.StandardOutput, "");
            var errorTask = ReadOutputAsync(process.StandardError, "[ошибка] ");
            await process.WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask);

            if (_cancelRequested)
            {
                FailureMessage = "Подготовка worktree была остановлена. Повторите отправку, чтобы запустить сценарий снова.";
                _status.Text = "Сценарий остановлен.";
                _cancelButton.Content = "Закрыть";
                _cancelButton.IsEnabled = true;
                return;
            }
            if (process.ExitCode != 0)
            {
                FailureMessage = $"Сценарий подготовки завершился с кодом {process.ExitCode}. Исправьте ошибку и повторите отправку.";
                _status.Text = FailureMessage;
                _status.Foreground = Brush("#F09999");
                _cancelButton.Content = "Закрыть";
                return;
            }

            Succeeded = true;
            _status.Text = "Подготовка завершена.";
            _status.Foreground = Brush("#92E5C7");
            Close();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            FailureMessage = "Не удалось выполнить setup-сценарий: " + error.Message;
            _status.Text = FailureMessage;
            _status.Foreground = Brush("#F09999");
            _cancelButton.Content = "Закрыть";
        }
        finally
        {
            _process = null;
        }
    }

    private async Task ReadOutputAsync(StreamReader reader, string prefix)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _output.AppendText(prefix + line + Environment.NewLine);
                if (_output.Text.Length > MaximumOutputCharacters)
                    _output.Text = _output.Text[^MaximumOutputCharacters..];
                _output.CaretIndex = _output.Text.Length;
                _output.ScrollToEnd();
            });
        }
    }

    private void CancelOrClose()
    {
        if (_process is { HasExited: false })
        {
            CancelProcess();
            return;
        }
        Close();
    }

    private void CancelProcess()
    {
        if (_cancelRequested) return;
        _status.Text = "Останавливаю сценарий…";
        _cancelButton.IsEnabled = false;
        try
        {
            if (_process is { HasExited: false } process)
                process.Kill(entireProcessTree: true);
            _cancelRequested = true;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            _cancelRequested = false;
            _status.Text = "Не удалось остановить сценарий: " + error.Message;
            _status.Foreground = Brush("#F09999");
            _cancelButton.IsEnabled = true;
        }
    }

    private static SolidColorBrush Brush(string value)
        => new((Color)ColorConverter.ConvertFromString(value));
}
