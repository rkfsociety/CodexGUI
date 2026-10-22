using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed record CodexExtension(string Id, string Name, string Description, string? Path,
    string? PluginId, bool Installed, bool Enabled, bool CanInstall)
{
    public string DisplayLabel => $"{Name} · {(Installed ? Enabled ? "включён" : "выключен" : "не установлен")}";
}

internal static class CodexExtensionCatalog
{
    public static async Task<IReadOnlyList<CodexExtension>> LoadPluginsAsync()
    {
        var json = await CodexCliService.RunPluginCommandAsync("list", "--available", "--json");
        using var document = JsonDocument.Parse(json);
        return ParsePlugins(document.RootElement);
    }

    internal static IReadOnlyList<CodexExtension> ParsePlugins(JsonElement root)
    {
        if (!root.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Codex CLI вернул неизвестный формат каталога плагинов.");

        var entries = installed.EnumerateArray().Concat(available.EnumerateArray()).ToArray();
        if (entries.Any(item => item.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("Каталог плагинов Codex CLI содержит некорректную запись.");

        return entries.Select(item => new CodexExtension(
                Text(item, "pluginId"), Text(item, "name"),
                $"{Text(item, "marketplaceName")} · {Text(item, "version")}",
                null, null, Flag(item, "installed"), Flag(item, "enabled"),
                Text(item, "installPolicy") == "AVAILABLE"))
            .Where(item => item.Id.Length > 0)
            .DistinctBy(item => item.Id)
            .OrderByDescending(item => item.Installed)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    internal static (IReadOnlyList<CodexExtension> Skills, IReadOnlyList<string> Errors) ParseSkills(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Codex CLI вернул неизвестный формат каталога скиллов.");

        var skills = new List<CodexExtension>();
        var errors = new List<string>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Каталог скиллов Codex содержит некорректную запись.");
            if (entry.TryGetProperty("errors", out var failures) && failures.ValueKind != JsonValueKind.Null)
            {
                if (failures.ValueKind != JsonValueKind.Array
                    || failures.EnumerateArray().Any(error => error.ValueKind != JsonValueKind.Object))
                    throw new InvalidDataException("Codex вернул некорректный список ошибок скиллов.");
                errors.AddRange(failures.EnumerateArray().Select(error => $"{Text(error, "path")}: {Text(error, "message")}"));
            }
            if (!entry.TryGetProperty("skills", out var items)) continue;
            if (items.ValueKind != JsonValueKind.Array
                || items.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object))
                throw new InvalidDataException("Codex вернул некорректный список скиллов.");
            foreach (var item in items.EnumerateArray())
            {
                var path = Text(item, "path");
                var plugin = Text(item, "pluginId");
                skills.Add(new CodexExtension(path, Text(item, "name"), Text(item, "description"),
                    path, plugin.Length > 0 ? plugin : null, true, Flag(item, "enabled"), false));
            }
        }
        return (skills.DistinctBy(item => item.Id)
            .OrderByDescending(item => item.Name.Contains("imagegen", StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains("computer-use", StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), errors);
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static bool Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
