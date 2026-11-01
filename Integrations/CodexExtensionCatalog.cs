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
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Codex CLI вернул неизвестный формат каталога плагинов.");

        var installedEntries = installed.EnumerateArray().ToArray();
        var availableEntries = available.EnumerateArray().ToArray();
        if (installedEntries.Concat(availableEntries).Any(item => item.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("Каталог плагинов Codex CLI содержит некорректную запись.");

        // The installed collection is authoritative even if its entries omit "installed".
        // Prefer it over the available collection when the same plugin appears in both.
        return installedEntries.Select(item => (Item: item, Installed: true))
            .Concat(availableEntries.Select(item => (Item: item, Installed: Flag(item, "installed"))))
            .Select(entry => new CodexExtension(
                Text(entry.Item, "pluginId"),
                string.IsNullOrWhiteSpace(Text(entry.Item, "name"))
                    ? Text(entry.Item, "pluginId") : Text(entry.Item, "name"),
                $"{Text(entry.Item, "marketplaceName")} · {Text(entry.Item, "version")}",
                null, null, entry.Installed, entry.Installed && Flag(entry.Item, "enabled"),
                !entry.Installed && Text(entry.Item, "installPolicy") == "AVAILABLE"))
            .Where(item => item.Id.Length > 0)
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Installed)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    internal static (IReadOnlyList<CodexExtension> Skills, IReadOnlyList<string> Errors) ParseSkills(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
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
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidDataException("В записи скилла Codex отсутствует путь.");
                var plugin = Text(item, "pluginId");
                var name = Text(item, "name");
                skills.Add(new CodexExtension(path, string.IsNullOrWhiteSpace(name) ? path : name,
                    Text(item, "description"), path, plugin.Length > 0 ? plugin : null,
                    true, Flag(item, "enabled"), false));
            }
        }
        return (skills.DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Name.Contains("imagegen", StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains("computer-use", StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), errors);
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static bool Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
