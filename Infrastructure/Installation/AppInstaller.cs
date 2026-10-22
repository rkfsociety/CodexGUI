using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace CodexGUI;

internal static class AppInstaller
{
    private const string ApplicationDirectoryName = "CodexGUI";
    private const string ExecutableName = "CodexGUI.exe";

    public static bool TryInstallAndLaunch()
    {
        var sourceExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(sourceExecutable)) return false;

        sourceExecutable = Path.GetFullPath(sourceExecutable);
        var installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            ApplicationDirectoryName);
        var installedExecutable = Path.Combine(installDirectory, ExecutableName);

        if (PathsEqual(sourceExecutable, installedExecutable) || IsDevelopmentCheckout(sourceExecutable)) return false;

        try
        {
            if (IsInstalledCopyRunning(installedExecutable))
            {
                SingleInstanceCoordinator.SignalPrimaryInstance();
                return true;
            }

            if (File.Exists(installedExecutable) && IsInstalledVersionCurrentOrNewer(installedExecutable))
            {
                TryCreateStartMenuShortcut(installedExecutable);
                LaunchInstalledCopy(installedExecutable, installDirectory);
                return true;
            }

            Directory.CreateDirectory(installDirectory);
            if (IsSingleFileApplication())
                File.Copy(sourceExecutable, installedExecutable, overwrite: true);
            else
                CopyApplicationDirectory(Path.GetDirectoryName(sourceExecutable)!, installDirectory);

            TryCreateStartMenuShortcut(installedExecutable);
            LaunchInstalledCopy(installedExecutable, installDirectory);
            return true;
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"Не удалось установить CodexGUI в папку пользователя. Приложение будет запущено из текущей папки.\n\n{error.Message}",
                "Установка CodexGUI",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private static bool IsSingleFileApplication() =>
        !File.Exists(Path.Combine(AppContext.BaseDirectory, "CodexGUI.dll"));

    private static bool IsInstalledVersionCurrentOrNewer(string installedExecutable)
    {
        var installedVersion = FileVersionInfo.GetVersionInfo(installedExecutable).ProductVersion?.Split('+')[0];
        return installedVersion is not null
            && AppUpdater.IsVersionFormat(installedVersion)
            && AppUpdater.CompareVersions(installedVersion, AppUpdater.CurrentVersion) >= 0;
    }

    private static bool IsInstalledCopyRunning(string installedExecutable)
    {
        if (!File.Exists(installedExecutable)) return false;

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(installedExecutable)))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule is { FileName: { } processPath } && PathsEqual(processPath, installedExecutable))
                        return true;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // An inaccessible process does not establish that the installed copy is running.
                }
            }
        }

        return false;
    }

    internal static bool IsDevelopmentCheckout(string executablePath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(executablePath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexGUI.csproj"))) return true;
            directory = directory.Parent;
        }

        return false;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void CopyApplicationDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            if (file.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            // Never recurse into a junction/symlink outside the distribution folder.
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            Directory.CreateDirectory(destination);
            CopyApplicationDirectory(directory, destination);
        }
    }

    private static void LaunchInstalledCopy(string executablePath, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory
        });
        if (process is null)
            throw new InvalidOperationException("Не удалось запустить установленный CodexGUI.");
    }

    private static void CreateStartMenuShortcut(string executablePath)
    {
        var programsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs");
        Directory.CreateDirectory(programsDirectory);
        var shortcutPath = Path.Combine(programsDirectory, "CodexGUI.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Не удалось создать ярлык меню Пуск.");
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Не удалось создать объект системного ярлыка.");
        object? shortcut = null;

        try
        {
            dynamic automation = shell;
            shortcut = automation.CreateShortcut(shortcutPath);
            dynamic link = shortcut;
            link.TargetPath = executablePath;
            link.WorkingDirectory = Path.GetDirectoryName(executablePath)!;
            link.IconLocation = executablePath + ",0";
            link.Description = "Codex Desktop";
            link.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void TryCreateStartMenuShortcut(string executablePath)
    {
        try
        {
            CreateStartMenuShortcut(executablePath);
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"CodexGUI установлен в папку пользователя, но ярлык меню Пуск создать не удалось.\n\n{error.Message}",
                "Установка CodexGUI",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
