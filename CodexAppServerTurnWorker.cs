using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed class CodexAppServerWorkerRequest
{
    public string RunId { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "high";
    public bool FullAccess { get; set; }
    public bool Unattended { get; set; }
    public string? ThreadId { get; set; }
    public string? ForkFromThreadId { get; set; }
    public string StartedPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string ErrorPath { get; set; } = "";
    public string CompletionPath { get; set; } = "";
}

internal sealed class CodexAppServerWorkerCompletion
{
    public int ExitCode { get; set; }
    public string? Error { get; set; }
    public string? SessionId { get; set; }
    public string? Status { get; set; }
}

internal static class CodexAppServerTurnWorker
{
    private const int UnsupportedRequestCode = -32601;

    public static async Task RunAsync(string requestPath)
    {
        CodexAppServerWorkerRequest? request = null;
        CodexAppServerWorkerCompletion? completion = null;
        Semaphore? workerGate = null;
        var ownsGate = false;
        try
        {
            request = JsonSerializer.Deserialize<CodexAppServerWorkerRequest>(await File.ReadAllTextAsync(requestPath))
                ?? throw new InvalidDataException("Не удалось прочитать запрос фонового процесса Codex.");
            completion = new CodexAppServerWorkerCompletion();
            workerGate = new Semaphore(1, 1, $@"Local\CodexGUI.AppServerWorker.{request.RunId}");
            ownsGate = workerGate.WaitOne(0);
            if (!ownsGate) return;

            try { File.Delete(requestPath); }
            catch (FileNotFoundException) { }
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            await File.WriteAllTextAsync(request.OutputPath, "", new UTF8Encoding(false));
            await File.WriteAllTextAsync(request.ErrorPath, "", new UTF8Encoding(false));
            var currentProcess = Process.GetCurrentProcess();
            await WriteJsonFileAsync(request.StartedPath, new
            {
                RunId = request.RunId,
                ProcessId = Environment.ProcessId,
                StartTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks
            });

            await using var connection = await CodexAppServerConnection.StartAsync(request.ExecutablePath);
            await connection.InitializeAsync();
            completion.SessionId = await StartOrResumeThreadAsync(connection, request);
            await WriteOutputEventAsync(request.OutputPath, "thread.started", new { thread_id = completion.SessionId });
            var sandboxPolicy = request.FullAccess
                ? new Dictionary<string, object> { ["type"] = "dangerFullAccess" }
                : new Dictionary<string, object> { ["type"] = "workspaceWrite", ["networkAccess"] = false };

            var turn = await connection.SendRequestAsync("turn/start", new
            {
                threadId = completion.SessionId,
                input = new[] { new { type = "text", text = request.Prompt } },
                model = request.Model,
                effort = request.Effort,
                approvalPolicy = request.FullAccess || request.Unattended ? "never" : "on-request",
                sandboxPolicy
            });
            var turnId = turn.TryGetProperty("turn", out var turnInfo) ? ReadString(turnInfo, "id") : null;
            if (turnId is null) throw new InvalidDataException("Codex app-server не вернул идентификатор хода.");
            await WriteOutputEventAsync(request.OutputPath, "turn.started", new { turn_id = turnId });

            while (true)
            {
                while (connection.TryReadIncoming(out var message))
                {
                    if (message.TryGetProperty("method", out var methodElement))
                    {
                        var method = methodElement.GetString() ?? "";
                        if (message.TryGetProperty("id", out var requestId))
                            await HandleServerRequestAsync(connection, request, message, method, requestId);
                        else if (method == "error" && message.TryGetProperty("params", out var errorParameters) &&
                            errorParameters.TryGetProperty("error", out var turnError))
                        {
                            completion.Error = ReadString(turnError, "message");
                            await WriteOutputEventAsync(request.OutputPath, "turn.error", new { message = completion.Error });
                        }
                        else if (await HandleNotificationAsync(request, message, method))
                        {
                            completion.Status = ReadTurnStatus(message);
                            completion.ExitCode = completion.Status == "completed" || completion.Status == "interrupted" ? 0 : 1;
                            if (completion.Status == "failed" && message.TryGetProperty("params", out var parameters)
                                && parameters.TryGetProperty("turn", out var failedTurn)
                                && failedTurn.TryGetProperty("error", out var error))
                                completion.Error ??= ReadString(error, "message");
                            if (completion.ExitCode != 0 && !string.IsNullOrWhiteSpace(completion.Error))
                                await File.WriteAllTextAsync(request.ErrorPath, completion.Error, new UTF8Encoding(false));
                            return;
                        }
                    }
                }

                await ProcessControlFilesAsync(connection, request, completion.SessionId, turnId);
                if (connection.ReaderTask.IsCompleted)
                    throw new EndOfStreamException("Codex app-server завершился до окончания хода.");
                await Task.Delay(50);
            }
        }
        catch (Exception error)
        {
            if (completion is not null)
            {
                completion.ExitCode = -1;
                completion.Error = error.Message;
            }
            if (request is not null && ownsGate)
            {
                try { await File.WriteAllTextAsync(request.ErrorPath, error.ToString(), new UTF8Encoding(false)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            if (ownsGate && request is not null && completion is not null)
            {
                try { await WriteJsonFileAsync(request.CompletionPath, completion); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                workerGate?.Release();
            }
            workerGate?.Dispose();
        }
    }

    private static async Task<string> StartOrResumeThreadAsync(CodexAppServerConnection connection, CodexAppServerWorkerRequest request)
    {
        var approvalPolicy = request.FullAccess || request.Unattended ? "never" : "on-request";
        var sandbox = request.FullAccess ? "danger-full-access" : "workspace-write";
        JsonElement result;
        if (!string.IsNullOrWhiteSpace(request.ForkFromThreadId))
        {
            result = await connection.SendRequestAsync("thread/fork", new
            {
                threadId = request.ForkFromThreadId,
                cwd = request.WorkingDirectory,
                model = request.Model,
                approvalPolicy,
                sandbox,
                config = new { model_reasoning_effort = request.Effort },
                excludeTurns = true
            });
        }
        else if (string.IsNullOrWhiteSpace(request.ThreadId))
        {
            result = await connection.SendRequestAsync("thread/start", new
            {
                cwd = request.WorkingDirectory,
                model = request.Model,
                approvalPolicy,
                sandbox,
                config = new { model_reasoning_effort = request.Effort }
            });
        }
        else
        {
            result = await connection.SendRequestAsync("thread/resume", new
            {
                threadId = request.ThreadId,
                model = request.Model,
                approvalPolicy,
                sandbox
            });
        }

        if (!result.TryGetProperty("thread", out var thread) || ReadString(thread, "id") is not { Length: > 0 } threadId)
            throw new InvalidDataException("Codex app-server не вернул идентификатор чата.");
        return threadId;
    }

    private static async Task<bool> HandleNotificationAsync(CodexAppServerWorkerRequest request, JsonElement message, string method)
    {
        if (!message.TryGetProperty("params", out var parameters)) return false;
        switch (method)
        {
            case "thread/started":
                if (parameters.TryGetProperty("thread", out var thread) && ReadString(thread, "id") is { } threadId)
                    await WriteOutputEventAsync(request.OutputPath, "thread.started", new { thread_id = threadId });
                break;
            case "turn/started":
                if (parameters.TryGetProperty("turn", out var startedTurn) && ReadString(startedTurn, "id") is { } startedTurnId)
                    await WriteOutputEventAsync(request.OutputPath, "turn.started", new { turn_id = startedTurnId });
                break;
            case "item/started":
            case "item/updated":
            case "item/completed":
                if (parameters.TryGetProperty("item", out var item))
                    await WriteOutputEventAsync(request.OutputPath, method.Replace('/', '.'), new { item });
                break;
            case "item/agentMessage/delta":
                if (ReadString(parameters, "delta") is { } delta)
                    await WriteOutputEventAsync(request.OutputPath, "response.output_text.delta", new { delta });
                break;
            case "turn/completed":
                await WriteOutputEventAsync(request.OutputPath, "turn.completed", new
                {
                    status = parameters.TryGetProperty("turn", out var completedTurn) ? ReadString(completedTurn, "status") : null
                });
                return true;
        }

        return false;
    }

    private static string? ReadTurnStatus(JsonElement message) =>
        message.TryGetProperty("params", out var parameters) && parameters.TryGetProperty("turn", out var turn)
            ? ReadString(turn, "status")
            : null;

    private static async Task HandleServerRequestAsync(CodexAppServerConnection connection, CodexAppServerWorkerRequest request,
        JsonElement message, string method, JsonElement requestId)
    {
        var isApproval = method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval";
        if (isApproval && message.TryGetProperty("params", out var parameters))
        {
            await WriteOutputEventAsync(request.OutputPath, "approval.request", new
            {
                request_id = requestId.GetRawText(),
                method,
                @params = parameters
            });
            return;
        }

        await WriteOutputEventAsync(request.OutputPath, "server.request.unsupported", new { method });
        await connection.SendErrorAsync(requestId, UnsupportedRequestCode, "CodexGUI does not support this app-server request yet.");
    }

    private static async Task ProcessControlFilesAsync(CodexAppServerConnection connection, CodexAppServerWorkerRequest request,
        string? threadId, string turnId)
    {
        var controlDirectory = Path.Combine(Path.GetDirectoryName(request.CompletionPath)!, "controls");
        if (!Directory.Exists(controlDirectory)) return;

        foreach (var path in Directory.EnumerateFiles(controlDirectory, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            CodexAppServerWorkerControl? control;
            try { control = JsonSerializer.Deserialize<CodexAppServerWorkerControl>(await File.ReadAllTextAsync(path)); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { continue; }
            if (control is null) continue;

            try
            {
                switch (control.Action)
                {
                    case "approval" when control.RequestId is { Length: > 0 } requestId && control.Decision is { Length: > 0 } decision:
                        using (var idDocument = JsonDocument.Parse(requestId))
                            await connection.SendResponseAsync(idDocument.RootElement, new { decision });
                        break;
                    case "interrupt" when threadId is { Length: > 0 }:
                        await connection.SendRequestAsync("turn/interrupt", new { threadId, turnId });
                        break;
                    case "steer" when threadId is { Length: > 0 } && control.Prompt is { Length: > 0 } prompt:
                        await connection.SendRequestAsync("turn/steer", new
                        {
                            threadId,
                            expectedTurnId = turnId,
                            input = new[] { new { type = "text", text = prompt } }
                        });
                        await WriteOutputEventAsync(request.OutputPath, "turn.steer.accepted", new { prompt });
                        break;
                    default:
                        continue;
                }
                File.Delete(path);
            }
            catch (Exception error)
            {
                await WriteOutputEventAsync(request.OutputPath, "control.error", new
                {
                    action = control.Action,
                    prompt = control.Prompt,
                    message = error.Message
                });
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task WriteOutputEventAsync(string path, string type, object payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        using var typeDocument = JsonDocument.Parse(JsonSerializer.Serialize(type));
        var properties = new Dictionary<string, JsonElement> { ["type"] = typeDocument.RootElement.Clone() };
        foreach (var property in document.RootElement.EnumerateObject()) properties[property.Name] = property.Value.Clone();
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteLineAsync(JsonSerializer.Serialize(properties));
        await writer.FlushAsync();
    }

    private static async Task WriteJsonFileAsync(string path, object value)
    {
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(value), new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal sealed class CodexAppServerWorkerControl
{
    public string Action { get; set; } = "";
    public string? RequestId { get; set; }
    public string? Decision { get; set; }
    public string? Prompt { get; set; }
}

internal sealed class CodexAppServerConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly Task<string> _standardErrorTask;
    private readonly Channel<JsonElement> _incoming = Channel.CreateUnbounded<JsonElement>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextRequestId;

    private CodexAppServerConnection(Process process)
    {
        _process = process;
        _writer = process.StandardInput;
        _standardErrorTask = process.StandardError.ReadToEndAsync();
        ReaderTask = Task.Run(ReadMessagesAsync);
    }

    public Task ReaderTask { get; }
    public static Task<CodexAppServerConnection> StartAsync(string executablePath)
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
        var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить Codex app-server.");
        return Task.FromResult(new CodexAppServerConnection(process));
    }

    public async Task InitializeAsync()
    {
        _ = await SendRequestAsync("initialize", new
        {
            clientInfo = new { name = "codexgui", title = "CodexGUI", version = AppUpdater.CurrentVersion }
        });
        await SendNotificationAsync("initialized", new { });
    }

    public bool TryReadIncoming(out JsonElement message) => _incoming.Reader.TryRead(out message);

    public async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var key = id.ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, completion)) throw new InvalidOperationException("Не удалось зарегистрировать запрос app-server.");
        try
        {
            await WriteMessageAsync(new { id, method, @params = parameters });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    public Task SendNotificationAsync(string method, object parameters) =>
        WriteMessageAsync(new { method, @params = parameters });

    public Task SendResponseAsync(JsonElement requestId, object result) =>
        WriteMessageAsync(new { id = requestId, result });

    public Task SendErrorAsync(JsonElement requestId, int code, string message) =>
        WriteMessageAsync(new { id = requestId, error = new { code, message } });

    private async Task WriteMessageAsync(object message)
    {
        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(message));
            await _writer.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadMessagesAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id))
                {
                    if (root.TryGetProperty("method", out _))
                    {
                        await _incoming.Writer.WriteAsync(root.Clone());
                        continue;
                    }
                    var key = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
                    if (key is not null && _pending.TryGetValue(key, out var completion))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            var message = ReadString(error, "message") ?? "Codex app-server отклонил запрос.";
                            completion.TrySetException(new InvalidOperationException(message));
                        }
                        else if (root.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                        else completion.TrySetException(new InvalidDataException("Codex app-server вернул пустой ответ."));
                    }
                    continue;
                }
                if (root.TryGetProperty("method", out _)) await _incoming.Writer.WriteAsync(root.Clone());
            }
            foreach (var pending in _pending.Values)
                pending.TrySetException(new EndOfStreamException("Codex app-server завершился до ответа."));
        }
        catch (Exception error)
        {
            foreach (var pending in _pending.Values) pending.TrySetException(error);
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        try { _writer.Close(); }
        catch (IOException) { }
        if (!_process.HasExited)
        {
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
        await ReaderTask;
        await _standardErrorTask;
        _process.Dispose();
        _writeLock.Dispose();
    }
}
