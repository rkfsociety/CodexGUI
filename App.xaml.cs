using System.Windows;

namespace CodexGUI;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
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

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        _singleInstance.ListenForActivation(mainWindow.ShowFromTray);
        Exit += (_, _) => _singleInstance.Dispose();
        mainWindow.Show();
    }
}
