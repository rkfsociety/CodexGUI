using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexGUI;

internal static class WorktreeSetupConfiguration
{
    private const string ConfigurationRelativePath = ".codex/codexgui-worktree.json";
    private const int MaximumConfigurationBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool Exists(string repositoryRoot)
        => File.Exists(Path.Combine(Path.GetFullPath(repositoryRoot), ConfigurationRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    public static string? ReadScriptPath(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var path = Path.Combine(root, ConfigurationRelativePath.Replace('/', Path.DirectorySeparatorChar));
        EnsureNoReparsePoints(root, path);
        if (!File.Exists(path)) return null;

        // Repository-controlled configuration must not cause unbounded memory allocation.
        // A read-only snapshot must not block an atomic update on Windows.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (stream.Length > MaximumConfigurationBytes)
            throw new InvalidDataException("Файл настройки worktree превышает допустимый размер 64 КиБ.");
        using var reader = new StreamReader(stream);
        var buffer = new char[MaximumConfigurationBytes + 1];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        if (length > MaximumConfigurationBytes)
            throw new InvalidDataException("Файл настройки worktree превышает допустимый размер 64 КиБ.");
        var configuration = JsonSerializer.Deserialize<Configuration>(new string(buffer, 0, length))
            ?? throw new InvalidDataException("Файл настройки worktree пуст.");
        if (string.IsNullOrWhiteSpace(configuration.SetupScript))
            throw new InvalidDataException("В настройке worktree не указан setupScript.");
        return ValidateRelativeScriptPath(repositoryRoot, configuration.SetupScript);
    }

    public static string SaveScriptPath(string repositoryRoot, string scriptPath)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var selectedScript = Path.GetFullPath(scriptPath);
        if (!File.Exists(selectedScript)) throw new FileNotFoundException("Setup-сценарий не найден.", selectedScript);
        var relativePath = ValidateRelativeScriptPath(root, Path.GetRelativePath(root, selectedScript));
        EnsureNoReparsePoints(root, selectedScript);

        var configurationPath = Path.Combine(root, ConfigurationRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
        EnsureNoReparsePoints(root, configurationPath);
        AtomicJsonFile.Write(configurationPath, new Configuration { SetupScript = relativePath }, JsonOptions);
        return relativePath;
    }

    public static string ResolveScriptPath(string worktreeRoot, string relativePath)
    {
        var root = Path.GetFullPath(worktreeRoot);
        var validatedPath = ValidateRelativeScriptPath(root, relativePath);
        var fullPath = Path.GetFullPath(Path.Combine(root, validatedPath));
        EnsureNoReparsePoints(root, fullPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                $"Сценарий «{validatedPath}» отсутствует в worktree. Добавьте и сохраните его в Git до создания новой рабочей копии.",
                fullPath);
        return fullPath;
    }

    public static void Remove(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var path = Path.Combine(root, ConfigurationRelativePath.Replace('/', Path.DirectorySeparatorChar));
        EnsureNoReparsePoints(root, path);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string ValidateRelativeScriptPath(string root, string path)
    {
        if (Path.IsPathRooted(path))
            throw new InvalidDataException("Путь setup-сценария должен быть относительным к проекту.");
        var normalized = Path.GetFullPath(Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, normalized);
        if (relative == "." || Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(relative), ".ps1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Укажите PowerShell-сценарий .ps1 внутри Git-репозитория.");
        return relative;
    }

    internal static void EnsureNoReparsePoints(string root, string targetPath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullTarget = Path.GetFullPath(targetPath);
        var relative = Path.GetRelativePath(fullRoot, fullTarget);
        if (relative == ".." || Path.IsPathRooted(relative)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Путь setup-сценария выходит за пределы репозитория.");

        // A junction or symlink at the repository root must not bypass the child checks.
        if ((File.Exists(fullRoot) || Directory.Exists(fullRoot))
            && (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Путь setup-сценария содержит junction или symbolic link.");

        var current = fullRoot;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Путь setup-сценария содержит junction или symbolic link.");
        }
    }

    private sealed class Configuration
    {
        public Configuration() { }

        [JsonPropertyName("setupScript")]
        public string SetupScript { get; set; } = "";
    }
}
