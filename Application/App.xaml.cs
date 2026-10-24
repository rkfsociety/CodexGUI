using System;
using System.Windows;

namespace CodexGUI;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--app-server-worker", StringComparison.Ordinal))
        {
            _ = RunWorkerAsync(e.Args[1]);
            return;
        }

        if (AppInstaller.TryInstallAndLaunch())
        {
            Shutdown();
            return;
        }

        _ = RunMainAsync();
    }

    private async Task RunWorkerAsync(string requestPath)
    {
        try
        {
            await CodexAppServerTurnWorker.RunAsync(requestPath);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("App server worker failed: {0}", error);
        }
        finally
        {
            Shutdown();
        }
    }

    private async Task RunMainAsync()
    {
        _singleInstance = SingleInstanceCoordinator.TryAcquire();
        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        var executablePath = Environment.ProcessPath;
        AppServerRuntimeSetupWindow? runtimeSetupWindow = null;
        try
        {
            var canUseDevelopmentCli = AppInstaller.IsDevelopmentCheckout(executablePath ?? "")
                && CodexAppServerRuntime.TryResolveInstalledCli(out _);
            if (!CodexAppServerRuntime.IsInstalled && !canUseDevelopmentCli)
            {
                runtimeSetupWindow = new AppServerRuntimeSetupWindow();
                runtimeSetupWindow.Show();
                var progress = new Progress<AppServerRuntimeProgress>(runtimeSetupWindow.Report);
                await CodexAppServerRuntime.EnsureInstalledAsync(progress);
            }
        }
        catch (Exception error)
        {
            if (runtimeSetupWindow?.IsVisible == true) runtimeSetupWindow.Close();
            if (CodexAppServerRuntime.TryResolveInstalledCli(out _))
            {
                CodexAppServerRuntime.UseInstalledCliFallback = true;
                CodexMessageDialog.Show(
                    $"\u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u043f\u043e\u0434\u0433\u043e\u0442\u043e\u0432\u0438\u0442\u044c App Server {CodexAppServerRuntime.Version}. \u0417\u0430\u043f\u0443\u0441\u043a\u0430\u0439\u0442\u0435 \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d\u043d\u0430\u044f \u0432\u0435\u0440\u0441\u0438\u044f Codex runtime.\n\n{error.Message}",
                    "CodexGUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                CodexMessageDialog.Show(
                    $"CodexGUI \u043d\u0435 \u0441\u043c\u043e\u0433 \u0437\u0430\u0433\u0440\u0443\u0437\u0438\u0442\u044c \u043e\u0431\u044f\u0437\u0430\u0442\u0435\u043b\u044c\u043d\u044b\u0439 App Server {CodexAppServerRuntime.Version}. \u041f\u0440\u043e\u0432\u0435\u0440\u044c\u0442\u0435 \u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u0438\u0435 \u043a \u0438\u043d\u0442\u0435\u0440\u043d\u0435\u0442\u0443 \u0438 \u0437\u0430\u043f\u0443\u0441\u043a\u0430\u0442\u044c \u0441\u043d\u043e\u0432\u0430.\n\n{error.Message}",
                    "CodexGUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }
        }
        finally
        {
            if (runtimeSetupWindow?.IsVisible == true) runtimeSetupWindow.Close();
        }

        try
        {
            if (!await CodexAppServerLogin.EnsureSignedInAsync())
            {
                CodexMessageDialog.Show("\u0412\u0445\u043e\u0434 \u0432 Codex \u043e\u0442\u043c\u0435\u043d\u0451\u043d. \u0414\u043b\u044f \u0437\u0430\u043f\u0443\u0441\u043a\u0430 CodexGUI \u0442\u0440\u0435\u0431\u0443\u0435\u0442\u0441\u044f \u0430\u0432\u0442\u043e\u0440\u0438\u0437\u0430\u0446\u0438\u044f.",
                    "\u0412\u0445\u043e\u0434 \u0432 Codex", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        catch (Exception error)
        {
            CodexMessageDialog.Show($"\u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u0437\u0430\u0432\u0435\u0440\u0448\u0438\u0442\u044c \u0432\u0445\u043e\u0434 \u0432 Codex.\n\n{error.Message}",
                "\u0412\u0445\u043e\u0434 \u0432 Codex", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        _singleInstance.ListenForActivation(mainWindow.ShowFromTray);
        Exit += (_, _) => _singleInstance.Dispose();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }

        _singleInstance = SingleInstanceCoordinator.TryAcquire();
        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        var executablePath = Environment.ProcessPath;
        AppServerRuntimeSetupWindow? runtimeSetupWindow = null;
        try
        {
            var canUseDevelopmentCli = AppInstaller.IsDevelopmentCheckout(executablePath ?? "")
                && CodexAppServerRuntime.TryResolveInstalledCli(out _);
            if (!CodexAppServerRuntime.IsInstalled && !canUseDevelopmentCli)
            {
                runtimeSetupWindow = new AppServerRuntimeSetupWindow();
                runtimeSetupWindow.Show();
                var progress = new Progress<AppServerRuntimeProgress>(runtimeSetupWindow.Report);
                await CodexAppServerRuntime.EnsureInstalledAsync(progress);
            }
        }
        catch (Exception error)
        {
            if (runtimeSetupWindow?.IsVisible == true) runtimeSetupWindow.Close();
            if (CodexAppServerRuntime.TryResolveInstalledCli(out _))
            {
                CodexAppServerRuntime.UseInstalledCliFallback = true;
                CodexMessageDialog.Show(
                    $"Не удалось подготовить App Server {CodexAppServerRuntime.Version}. Запущена установленная версия Codex runtime.\n\n{error.Message}",
                    "CodexGUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                CodexMessageDialog.Show(
                    $"CodexGUI не смог загрузить обязательный App Server {CodexAppServerRuntime.Version}. Проверьте подключение к интернету и запустите приложение снова.\n\n{error.Message}",
                    "CodexGUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }
        }
        finally
        {
            if (runtimeSetupWindow?.IsVisible == true) runtimeSetupWindow.Close();
        }

        try
        {
            if (!await CodexAppServerLogin.EnsureSignedInAsync())
            {
                CodexMessageDialog.Show("Вход в Codex отменён. Для запуска CodexGUI требуется авторизация.",
                    "Вход в Codex", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        catch (Exception error)
        {
            CodexMessageDialog.Show($"Не удалось завершить вход в Codex.\n\n{error.Message}",
                "Вход в Codex", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        _singleInstance.ListenForActivation(mainWindow.ShowFromTray);
        Exit += (_, _) => _singleInstance.Dispose();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }
}
