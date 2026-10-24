using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed class CodexAppServerClient(string executablePath, CancellationToken lifetimeToken = default)
{
    private string InitializeRequest => JsonSerializer.Serialize(new
    {
        id = 1,
        method = "initialize",
        @params = new { clientInfo = new { name = "codexgui", title = "CodexGUI", version = AppUpdater.CurrentVersion } }
    });

    public Task<JsonElement> ReadRateLimitsAsync(CancellationToken cancellationToken = default) => SendRequestAsync("account/rateLimits/read", new { }, cancellationToken);

    public Task<JsonElement> ReadAccountAsync(CancellationToken cancellationToken = default) => SendRequestAsync("account/read", new { refreshToken = false }, cancellationToken);

    public Task<JsonElement> ReadThreadGoalAsync(string sessionId, CancellationToken cancellationToken = default) =>
        SendRequestAsync("thread/goal/get", new { threadId = sessionId }, cancellationToken);

    public Task<JsonElement> SetThreadGoalAsync(string sessionId, string objective, string status, ulong? tokenBudget, CancellationToken cancellationToken = default) =>
        SendRequestAsync("thread/goal/set", new
        {
            threadId = sessionId,
            origin = "user",
            objective,
            status,
            tokenBudget
        }, cancellationToken);

    public Task<JsonElement> ClearThreadGoalAsync(string sessionId, CancellationToken cancellationToken = default) =>
        SendRequestAsync("thread/goal/clear", new { threadId = sessionId, origin = "user" }, cancellationToken);

    public Task<JsonElement> ReadSkillsAsync(string projectPath, CancellationToken cancellationToken = default) => SendRequestAsync("skills/list", new
    {
        cwds = new[] { projectPath },
        forceReload = true
    }, cancellationToken);

    public Task<JsonElement> SetSkillEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default) =>
        SendRequestAsync("skills/config/write", new { path, enabled }, cancellationToken);

    public Task<JsonElement> SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default) =>
        SendRequestAsync("config/value/write", new
        {
            keyPath = $"plugins.{JsonSerializer.Serialize(pluginId)}.enabled",
            value = enabled,
            mergeStrategy = "replace"
        }, cancellationToken);

    public Task<JsonElement> UpgradeMarketplacesAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync("marketplace/upgrade", new { }, cancellationToken);

    public Task<JsonElement> AddMarketplaceAsync(string source, string? refName, IReadOnlyList<string> sparsePaths, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Не указан источник маркетплейса.", nameof(source));
        var parameters = new Dictionary<string, object> { ["source"] = source.Trim() };
        if (!string.IsNullOrWhiteSpace(refName)) parameters["refName"] = refName.Trim();
        var paths = sparsePaths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length > 0) parameters["sparsePaths"] = paths;
        return SendRequestAsync("marketplace/add", parameters, cancellationToken);
    }

    public Task<JsonElement> RemoveMarketplaceAsync(string marketplaceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(marketplaceName))
            throw new ArgumentException("Не указано имя маркетплейса.", nameof(marketplaceName));
        return SendRequestAsync("marketplace/remove", new { marketplaceName = marketplaceName.Trim() }, cancellationToken);
    }

    public Task<IReadOnlyList<JsonElement>> ListMcpServerStatusesAsync(CancellationToken cancellationToken = default)
        => RunWithSessionAsync<IReadOnlyList<JsonElement>>(async send =>
    {
        var statuses = new List<JsonElement>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var pageCount = 0;
        do
        {
            if (++pageCount > 100)
                throw new InvalidDataException("Список MCP-серверов превысил допустимое число страниц.");
            var response = await send("mcpServerStatus/list", new
            {
                cursor,
                detail = "toolsAndAuthOnly",
                limit = 100
            });
            if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Codex App Server вернул некорректный список MCP-серверов.");
            foreach (var status in data.EnumerateArray())
            {
                if (status.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Список MCP-серверов содержит некорректную запись.");
                statuses.Add(status.Clone());
            }

            cursor = ReadCursor(response);
            if (cursor is not null && !seenCursors.Add(cursor))
                throw new InvalidDataException("Codex App Server повторил курсор списка MCP-серверов.");
        } while (cursor is not null);

        return statuses;
    }, cancellationToken);

    public async Task<JsonElement> ReadThreadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await RunWithSessionAsync(async send =>
        {
            var metadata = await send("thread/read", new { threadId = sessionId });
            if (!metadata.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Codex App Server не вернул метаданные чата.");

            var turns = new List<JsonElement>();
            string? cursor = null;
            var page = 0;
            do
            {
                var response = await send("thread/turns/list", new
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
                cursor = ReadCursor(response);
                if (++page >= 200 && !string.IsNullOrEmpty(cursor))
                    throw new InvalidDataException("История чата слишком велика для восстановления в одном окне.");
            }
            while (!string.IsNullOrEmpty(cursor));

            turns.Reverse();
            var threadFields = thread.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            threadFields["turns"] = JsonSerializer.SerializeToElement(turns);
            return JsonSerializer.SerializeToElement(new { thread = threadFields });
        }, cancellationToken);
    }

    public Task<JsonElement> ForkThreadAsync(string sessionId, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object>
        {
            ["threadId"] = sessionId,
            ["excludeTurns"] = true
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) parameters["cwd"] = workingDirectory;
        return SendRequestAsync("thread/fork", parameters, cancellationToken);
    }

    public Task<JsonElement> ArchiveThreadAsync(string sessionId, CancellationToken cancellationToken = default) => RunWithSessionAsync(async send =>
    {
        try
        {
            return await send("thread/archive", new { threadId = sessionId });
        }
        catch (InvalidOperationException error) when (
            string.Equals(error.Message, $"no rollout found for thread id {sessionId}", StringComparison.Ordinal))
        {
            // Another Codex client may have archived the shared rollout since our local state was saved.
            // Only reconcile after the server confirms it; a genuinely missing history must still fail.
            string? cursor = null;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var response = await send("thread/list", new
                {
                    archived = true,
                    limit = 100,
                    modelProviders = Array.Empty<string>(),
                    sourceKinds = new[] { "cli", "vscode", "exec", "appServer", "subAgent", "subAgentReview",
                        "subAgentCompact", "subAgentThreadSpawn", "subAgentOther", "unknown" },
                    cursor
                });
                if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Codex App Server вернул некорректный список архивных чатов.");
                if (data.EnumerateArray().Any(thread => Text(thread, "id") == sessionId))
                    return JsonSerializer.SerializeToElement(new { });
                cursor = ReadCursor(response);
                if (!string.IsNullOrEmpty(cursor) && !seenCursors.Add(cursor))
                    throw new InvalidDataException("Codex App Server повторил курсор списка архивных чатов.");
            }
            while (!string.IsNullOrEmpty(cursor));
            throw;
        }
    }, cancellationToken);

    public Task<JsonElement> UnarchiveThreadAsync(string sessionId, CancellationToken cancellationToken = default) => SendRequestAsync("thread/unarchive", new { threadId = sessionId }, cancellationToken);

    public Task<JsonElement> DeleteThreadAsync(string sessionId, CancellationToken cancellationToken = default) => SendRequestAsync("thread/delete", new { threadId = sessionId }, cancellationToken);

    public async Task<IReadOnlyList<ModelOption>> ReadModelCatalogAsync(string defaultEffort, CancellationToken cancellationToken = default)
    {
        return await RunWithSessionAsync(async send =>
        {
            var models = new List<ModelOption>();
            string? cursor = null;
            var page = 0;
            do
            {
                var response = await send("model/list", new { limit = 100, includeHidden = false, cursor });
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

                cursor = ReadCursor(response);
                if (++page >= 20 && !string.IsNullOrEmpty(cursor))
                    throw new InvalidDataException("Каталог моделей Codex App Server содержит слишком много страниц.");
            }
            while (!string.IsNullOrEmpty(cursor));

            return models;
        }, cancellationToken);
    }

    private Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken = default) =>
        RunWithSessionAsync(send => send(method, parameters), cancellationToken);

    // Keep one App Server process alive for multi-page reads instead of restarting it for every page.
    private async Task<T> RunWithSessionAsync<T>(
        Func<Func<string, object, Task<JsonElement>>, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        lifetimeToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        using var sessionDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, cancellationToken);
        sessionDeadline.CancelAfter(TimeSpan.FromMinutes(10));
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
        var errorsTask = DrainStandardErrorAsync(process.StandardError);
        var standardOutput = new BoundedTextLineReader(process.StandardOutput);
        try
        {
            await WriteRpcLineAsync(process.StandardInput, InitializeRequest, sessionDeadline.Token);
            _ = await ReadRpcResultAsync(standardOutput, 1, sessionDeadline.Token);
            await WriteRpcLineAsync(process.StandardInput, "{\"method\":\"initialized\",\"params\":{}}", sessionDeadline.Token);

            var nextRequestId = 1;
            async Task<JsonElement> SendAsync(string requestMethod, object requestParameters)
            {
                var id = ++nextRequestId;
                var request = JsonSerializer.Serialize(new { id, method = requestMethod, @params = requestParameters });
                await WriteRpcLineAsync(process.StandardInput, request, sessionDeadline.Token);
                return await ReadRpcResultAsync(standardOutput, id, sessionDeadline.Token);
            }

            return await operation(SendAsync);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && !lifetimeToken.IsCancellationRequested && sessionDeadline.IsCancellationRequested)
        {
            throw new TimeoutException("Операция Codex app-server не завершилась за 10 минут.", error);
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException) { }
                }
            }
            // Ignore lingering inherited stderr handles during shutdown.
            try { await errorsTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
        }
    }

    private static async Task DrainStandardErrorAsync(StreamReader reader)
    {
        var buffer = new char[8192];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory()) != 0) { }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            System.Diagnostics.Trace.TraceWarning("Ошибка при чтении stderr Codex app-server: {0}", error.Message);
        }
    }

    private static async Task WriteRpcLineAsync(StreamWriter writer, string line, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), timeout.Token);
            await writer.FlushAsync(timeout.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Не удалось отправить запрос Codex app-server за 20 секунд.", error);
        }
    }

    private static async Task<JsonElement> ReadRpcResultAsync(BoundedTextLineReader reader, int requestId,
        CancellationToken cancellationToken)
    {
        // A single deadline prevents unrelated notifications from keeping an RPC alive forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.Number
                    || !id.TryGetInt32(out var responseId)
                    || responseId != requestId)
                    continue;
                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                {
                    var message = error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("message", out var text)
                        && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                    throw new InvalidOperationException(message ?? "Codex app-server отклонил запрос.");
                }
                if (root.TryGetProperty("result", out var result)) return result.Clone();
                throw new InvalidDataException("Codex app-server вернул пустой ответ.");
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Codex app-server не ответил на запрос за 20 секунд.", error);
        }

        throw new EndOfStreamException("Codex app-server завершился до ответа.");
    }

    private static string? ReadCursor(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Codex App Server вернул некорректную страницу данных.");
        if (!response.TryGetProperty("nextCursor", out var cursor) || cursor.ValueKind == JsonValueKind.Null)
            return null;
        if (cursor.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Codex App Server вернул некорректный курсор следующей страницы.");
        return cursor.GetString();
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
