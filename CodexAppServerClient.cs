using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed class CodexAppServerClient(string executablePath)
{
    private string InitializeRequest => JsonSerializer.Serialize(new
    {
        id = 1,
        method = "initialize",
        @params = new { clientInfo = new { name = "codexgui", title = "CodexGUI", version = AppUpdater.CurrentVersion } }
    });

    public Task<JsonElement> ReadRateLimitsAsync() => SendRequestAsync("account/rateLimits/read", new { });

    public Task<JsonElement> ReadAccountAsync() => SendRequestAsync("account/read", new { refreshToken = false });

    public Task<JsonElement> ReadSkillsAsync(string projectPath) => SendRequestAsync("skills/list", new
    {
        cwds = new[] { projectPath },
        forceReload = true
    });

    public Task<JsonElement> SetSkillEnabledAsync(string path, bool enabled) =>
        SendRequestAsync("skills/config/write", new { path, enabled });

    public Task<JsonElement> SetPluginEnabledAsync(string pluginId, bool enabled) =>
        SendRequestAsync("config/value/write", new
        {
            keyPath = $"plugins.{JsonSerializer.Serialize(pluginId)}.enabled",
            value = enabled,
            mergeStrategy = "replace"
        });

    public async Task<JsonElement> ReadThreadAsync(string sessionId)
    {
        var metadata = await SendRequestAsync("thread/read", new { threadId = sessionId });
        if (!metadata.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Codex App Server не вернул метаданные чата.");

        var turns = new List<JsonElement>();
        string? cursor = null;
        var page = 0;
        do
        {
            var response = await SendRequestAsync("thread/turns/list", new
            {
                threadId = sessionId,
                limit = 50,
                sortDirection = "desc",
                itemsView = "full",
                cursor
            });
            if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Codex App Server вернул неизвестный формат истории чата.");
            turns.AddRange(data.EnumerateArray().Select(item => item.Clone()));
            cursor = Text(response, "nextCursor");
            if (++page >= 200 && cursor.Length > 0)
                throw new InvalidDataException("История чата слишком велика для восстановления в одном окне.");
        }
        while (cursor.Length > 0);

        turns.Reverse();
        using var turnsDocument = JsonDocument.Parse(JsonSerializer.Serialize(turns));
        var threadFields = thread.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        threadFields["turns"] = turnsDocument.RootElement.Clone();
        using var historyDocument = JsonDocument.Parse(JsonSerializer.Serialize(new { thread = threadFields }));
        return historyDocument.RootElement.Clone();
    }

    public Task<JsonElement> ForkThreadAsync(string sessionId, string? workingDirectory = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["threadId"] = sessionId,
            ["excludeTurns"] = true
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) parameters["cwd"] = workingDirectory;
        return SendRequestAsync("thread/fork", parameters);
    }

    public Task<JsonElement> ArchiveThreadAsync(string sessionId) => SendRequestAsync("thread/archive", new { threadId = sessionId });

    public Task<JsonElement> UnarchiveThreadAsync(string sessionId) => SendRequestAsync("thread/unarchive", new { threadId = sessionId });

    public Task<JsonElement> DeleteThreadAsync(string sessionId) => SendRequestAsync("thread/delete", new { threadId = sessionId });

    public async Task<IReadOnlyList<ModelOption>> ReadModelCatalogAsync(string defaultEffort)
    {
        var models = new List<ModelOption>();
        string? cursor = null;
        var page = 0;
        do
        {
            var response = await SendRequestAsync("model/list", new { limit = 100, includeHidden = false, cursor });
            if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Codex App Server вернул неизвестный формат каталога моделей.");

            foreach (var item in data.EnumerateArray())
            {
                var slug = Text(item, "id");
                if (slug.Length == 0) slug = Text(item, "model");
                if (slug.Length == 0 || item.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
                    continue;
                var effort = Text(item, "defaultReasoningEffort");
                if (effort.Length == 0) effort = defaultEffort;
                var reasoningLevels = item.TryGetProperty("supportedReasoningEfforts", out var efforts) && efforts.ValueKind == JsonValueKind.Array
                    ? efforts.EnumerateArray().Select(value => Text(value, "reasoningEffort"))
                        .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray()
                    : [];
                if (reasoningLevels.Length == 0) reasoningLevels = [effort];
                models.Add(new ModelOption(
                    slug,
                    Text(item, "displayName") is { Length: > 0 } displayName ? displayName : slug,
                    effort,
                    reasoningLevels,
                    models.Count));
            }

            cursor = Text(response, "nextCursor");
            if (++page >= 20 && cursor.Length > 0)
                throw new InvalidDataException("Каталог моделей Codex App Server содержит слишком много страниц.");
        }
        while (cursor.Length > 0);

        return models;
    }

    private async Task<JsonElement> SendRequestAsync(string method, object parameters)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        CodexAppServerRuntime.AddProcessArguments(startInfo, executablePath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить Codex app-server.");
        var errorsTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(InitializeRequest);
            _ = await ReadRpcResultAsync(process.StandardOutput, 1);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}");
            var request = JsonSerializer.Serialize(new { id = 2, method, @params = parameters });
            await process.StandardInput.WriteLineAsync(request);
            return await ReadRpcResultAsync(process.StandardOutput, 2);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); }
            }
            await errorsTask;
        }
    }

    private static async Task<JsonElement> ReadRpcResultAsync(StreamReader reader, int requestId)
    {
        while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() != requestId)
                continue;
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : null;
                throw new InvalidOperationException(message ?? "Codex app-server отклонил запрос.");
            }
            if (root.TryGetProperty("result", out var result)) return result.Clone();
            throw new InvalidDataException("Codex app-server вернул пустой ответ.");
        }

        throw new EndOfStreamException("Codex app-server завершился до ответа.");
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
