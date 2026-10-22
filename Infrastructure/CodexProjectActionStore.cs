using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexGUI;

internal static class CodexProjectActionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static List<CodexActionDefinition> Load(string projectPath, out string? revision)
    {
        revision = null;
        var (root, configurationPath) = GetPaths(projectPath);
        WorktreeSetupConfiguration.EnsureNoReparsePoints(root, configurationPath);
        if (!File.Exists(configurationPath)) return [];

        var contents = File.ReadAllText(configurationPath);
        revision = GetRevision(contents);
        var storedActions = JsonSerializer.Deserialize<List<CodexActionDefinition?>>(contents, JsonOptions)
            ?? throw new InvalidDataException("Файл действий проекта пуст.");
        Validate(storedActions);
        return storedActions.Cast<CodexActionDefinition>().ToList();
    }

    public static string Save(string projectPath, IReadOnlyCollection<CodexActionDefinition> actions, string? expectedRevision)
    {
        var (root, configurationPath) = GetPaths(projectPath);
        Validate(actions);
        WorktreeSetupConfiguration.EnsureNoReparsePoints(root, configurationPath);
        var currentRevision = File.Exists(configurationPath)
            ? GetRevision(File.ReadAllText(configurationPath))
            : null;
        if (!string.Equals(currentRevision, expectedRevision, StringComparison.Ordinal))
            throw new IOException("Файл действий проекта изменился после открытия окна. Закройте и откройте окно снова, чтобы загрузить актуальные действия.");

        var serialized = JsonSerializer.Serialize(actions, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
        WorktreeSetupConfiguration.EnsureNoReparsePoints(root, configurationPath);
        AtomicJsonFile.Write(configurationPath, actions, JsonOptions);
        return GetRevision(serialized);
    }

    private static (string Root, string ConfigurationPath) GetPaths(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException("Не указан путь проекта.", nameof(projectPath));

        var root = Path.GetFullPath(projectPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        return (root, Path.Combine(root, ".codex", "codexgui-actions.json"));
    }

    private static void Validate(IEnumerable<CodexActionDefinition?> actions)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            if (action is null
                || string.IsNullOrWhiteSpace(action.Id)
                || string.IsNullOrWhiteSpace(action.Name)
                || action.Name.Length > 80
                || string.IsNullOrWhiteSpace(action.Prompt)
                || action.Prompt.Length > 12000)
                throw new InvalidDataException("Файл действий проекта содержит пустое или слишком длинное действие.");
            if (!ids.Add(action.Id) || !names.Add(action.Name))
                throw new InvalidDataException("Файл действий проекта содержит повторяющиеся идентификаторы или названия.");
        }
    }

    private static string GetRevision(string contents)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents)));
}
