using System.Windows;

namespace CodexGUI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (AppInstaller.TryInstallAndLaunch())
        {
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
