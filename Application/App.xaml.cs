using System;
using System.Windows;

namespace CodexGUI;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--app-server-worker", StringComparison.Ordinal))
        {
            await CodexAppServerTurnWorker.RunAsync(e.Args[1]);
            Shutdown();
            return;
        }

        if (AppInstaller.TryInstallAndLaunch())
        {
            Shutdown();
            return;
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
