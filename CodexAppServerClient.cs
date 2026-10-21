using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed class CodexAppServerClient(string executablePath)
{
    private const string InitializeRequest = "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codexgui\",\"title\":\"CodexGUI\",\"version\":\"1.0\"}}}";

    public Task<JsonElement> ReadRateLimitsAsync() => SendRequestAsync("account/rateLimits/read", new { });

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

    public Task<JsonElement> ReadThreadAsync(string sessionId) => SendRequestAsync("thread/read", new
    {
        threadId = sessionId,
        includeTurns = true
    });

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
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

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
}
