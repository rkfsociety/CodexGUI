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
