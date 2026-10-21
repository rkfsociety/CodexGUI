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

        var updateInstallerStarted = false;
        string? updateCheckError = null;
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath) && !AppInstaller.IsDevelopmentCheckout(executablePath))
        {
            PreparedAppUpdate? preparedUpdate = null;
            try
            {
                if (await AppUpdater.FindUpdateAsync() is { } update)
                {
                    preparedUpdate = await AppUpdater.DownloadAndVerifyAsync(update);
                    await AppUpdater.InstallPreparedUpdateAsync(preparedUpdate);
                    preparedUpdate = null;
                    updateInstallerStarted = true;
                }
            }
            catch (Exception error)
            {
                if (preparedUpdate is not null) AppUpdater.DiscardPreparedUpdate(preparedUpdate);
                updateCheckError = error.Message;
            }
        }

        if (updateInstallerStarted)
        {
            Shutdown();
            return;
        }

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
                MessageBox.Show(
                    $"Не удалось подготовить App Server {CodexAppServerRuntime.Version}. Запущена установленная версия Codex runtime.\n\n{error.Message}",
                    "CodexGUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
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
                MessageBox.Show("Вход в Codex отменён. Для запуска CodexGUI требуется авторизация.",
                    "Вход в Codex", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        catch (Exception error)
        {
            MessageBox.Show($"Не удалось завершить вход в Codex.\n\n{error.Message}",
                "Вход в Codex", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        if (updateCheckError is not null)
            MessageBox.Show(
                $"Не удалось проверить или подготовить обновление. Запущена текущая версия.\n\n{updateCheckError}",
                "Обновление CodexGUI",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        _singleInstance.ListenForActivation(mainWindow.ShowFromTray);
        Exit += (_, _) => _singleInstance.Dispose();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }
}
