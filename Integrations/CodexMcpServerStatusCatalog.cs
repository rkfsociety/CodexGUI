using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed record CodexMcpServerStatus(
    string Name,
    string? RuntimeStatus,
    string AuthStatus,
    IReadOnlyList<string> ToolNames,
    string? ToolsError,
    string? ServerTitle,
    string? ServerVersion,
    string? HttpOrigin)
{
    public string ListSummary => $"{StateLabel} · инструментов: {ToolNames.Count}";

    public string StateLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ToolsError)) return "Ошибка проверки";
            return RuntimeStatus switch
            {
                "connected" => "Подключён",
                "starting" => "Подключается",
                "authenticationRequired" => "Требуется вход",
                "failed" => "Ошибка подключения",
                "cancelled" => "Подключение отменено",
                "disabled" => "Отключён",
                "notStarted" => "Не запускался",
                null when AuthStatus == "notLoggedIn" => "Требуется вход",
                null when ServerTitle is not null || ToolNames.Count > 0 => "Доступен",
                null => "Нет данных",
                _ => RuntimeStatus
            };
        }
    }

    public string AuthenticationLabel => AuthStatus switch
    {
        "unknown" => "Неизвестно",
        "unsupported" => "Не поддерживается",
        "notLoggedIn" => "Вход не выполнен",
        "bearerToken" => "Bearer-токен",
        "oAuth" => "OAuth",
        _ => AuthStatus
    };
}

internal static class CodexMcpServerStatusCatalog
{
    public static async Task<IReadOnlyList<CodexMcpServerStatus>> LoadAsync(CodexAppServerClient client)
        => ParseEntries(await client.ListMcpServerStatusesAsync());

    internal static IReadOnlyList<CodexMcpServerStatus> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Codex App Server вернул некорректный список MCP-серверов.");

        return ParseEntries(root.EnumerateArray());
    }

    private static IReadOnlyList<CodexMcpServerStatus> ParseEntries(IEnumerable<JsonElement> entries)
    {
        var statuses = new List<CodexMcpServerStatus>();
        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Список MCP-серверов содержит некорректную запись.");

            var name = ReadRequiredString(entry, "name");
            var authStatus = ReadRequiredString(entry, "authStatus");
            var toolNames = ReadToolNames(entry);
            var serverInfo = entry.TryGetProperty("serverInfo", out var info) && info.ValueKind == JsonValueKind.Object
                ? info
                : default;
            statuses.Add(new CodexMcpServerStatus(
                name,
                ReadOptionalString(entry, "runtimeStatus"),
                authStatus,
                toolNames,
                ReadOptionalString(entry, "toolsError"),
                serverInfo.ValueKind == JsonValueKind.Object ? ReadOptionalString(serverInfo, "title") : null,
                serverInfo.ValueKind == JsonValueKind.Object ? ReadOptionalString(serverInfo, "version") : null,
                ReadOptionalString(entry, "httpOrigin")));
        }

        return statuses
            .OrderBy(status => status.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string ReadRequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"В записи MCP-сервера отсутствует поле {name}.");
        return property.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Поле {name} в записи MCP-сервера должно быть строкой или null.");
        return property.GetString();
    }

    private static IReadOnlyList<string> ReadToolNames(JsonElement value)
    {
        if (!value.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("В записи MCP-сервера отсутствует список инструментов.");
        return tools.EnumerateObject()
            .Select(tool => tool.Name)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
