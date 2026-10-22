using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed record CodexMarketplace(string Name, string Root);

internal static class CodexMarketplaceCatalog
{
    public static async Task<IReadOnlyList<CodexMarketplace>> LoadAsync()
        => Parse(await CodexCliService.RunPluginCommandAsync("marketplace", "list", "--json"));

    internal static IReadOnlyList<CodexMarketplace> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("marketplaces", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Codex CLI вернул неизвестный формат списка маркетплейсов.");

        var marketplaces = new List<CodexMarketplace>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Список маркетплейсов Codex CLI содержит некорректную запись.");
            var name = ReadString(entry, "name");
            var path = ReadString(entry, "root");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException("В записи маркетплейса отсутствует имя или путь.");
            marketplaces.Add(new CodexMarketplace(name, path));
        }

        return marketplaces
            .OrderBy(marketplace => marketplace.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(marketplace => marketplace.Root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ReadString(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? ""
            : "";
}
