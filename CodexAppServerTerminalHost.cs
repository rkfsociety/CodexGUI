using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed record CodexTerminalOutput(string ProcessId, string Text);
internal sealed record CodexTerminalExit(string ProcessId, int ExitCode, string? Error);

internal sealed class CodexAppServerTerminalHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private CodexAppServerConnection? _connection;
    private Task? _notificationTask;
    private int _disposed;

    public event Action<CodexTerminalOutput>? OutputReceived;
    public event Action<CodexTerminalExit>? SessionExited;

    public async Task StartSessionAsync(string processId, string workingDirectory, bool fullAccess, int rows = 28, int cols = 100)
    {
        await EnsureConnectedAsync();
        await _startLock.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CodexAppServerTerminalHost));
            var connection = _connection ?? throw new InvalidOperationException("Codex terminal не подключён к App Server.");
            if (!_sessions.TryAdd(processId, 0)) throw new InvalidOperationException("Терминал с таким идентификатором уже запущен.");
            object sandboxPolicy = fullAccess
                ? new { type = "dangerFullAccess" }
                : new { type = "workspaceWrite", networkAccess = false };
            var request = new
            {
                command = new[] { "powershell.exe", "-NoLogo", "-NoProfile", "-NoExit" },
                processId,
                tty = true,
                streamStdin = true,
                streamStdoutStderr = true,
                disableTimeout = true,
                cwd = workingDirectory,
                sandboxPolicy,
                size = new { rows = Math.Clamp(rows, 8, 120), cols = Math.Clamp(cols, 30, 300) }
            };
            var responseTask = connection.SendLongRunningRequestAsync("command/exec", request);
            _ = ObserveSessionAsync(processId, responseTask);
        }
        catch
        {
            _sessions.TryRemove(processId, out _);
            throw;
        }
        finally { _startLock.Release(); }
    }

    public async Task WriteInputAsync(string processId, string text)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CodexAppServerTerminalHost));
        if (!_sessions.ContainsKey(processId)) throw new InvalidOperationException("Терминал уже завершён.");
        var connection = _connection ?? throw new InvalidOperationException("Codex terminal не подключён к App Server.");
        await connection.SendRequestAsync("command/exec/write", new
        {
            processId,
            deltaBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
        });
    }

    public async Task ResizeAsync(string processId, int rows, int cols)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_sessions.ContainsKey(processId)) return;
        var connection = _connection ?? throw new InvalidOperationException("Codex terminal не подключён к App Server.");
        await connection.SendRequestAsync("command/exec/resize", new
        {
            processId,
            size = new { rows = Math.Clamp(rows, 8, 120), cols = Math.Clamp(cols, 30, 300) }
        });
    }

    public async Task TerminateSessionAsync(string processId)
    {
        if (!_sessions.ContainsKey(processId) || _connection is not { } connection) return;
        try { await connection.SendRequestAsync("command/exec/terminate", new { processId }); }
        catch (Exception error) when (error is InvalidOperationException or EndOfStreamException or TimeoutException)
        {
            System.Diagnostics.Trace.TraceWarning("Не удалось завершить терминал {0}: {1}", processId, error.Message);
        }
    }

    private async Task EnsureConnectedAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CodexAppServerTerminalHost));
        if (_connection is not null) return;
        await _startLock.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CodexAppServerTerminalHost));
            if (_connection is not null) return;
            var connection = await CodexAppServerConnection.StartAsync(CodexAppServerRuntime.ResolveExecutable());
            try { await connection.InitializeAsync(); }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
            _connection = connection;
            _notificationTask = Task.Run(() => ReadNotificationsAsync(connection, _lifetime.Token));
        }
        finally { _startLock.Release(); }
    }

    private async Task ReadNotificationsAsync(CodexAppServerConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !connection.ReaderTask.IsCompleted)
            {
                while (connection.TryReadIncoming(out var message))
                {
                    if (GetString(message, "method") != "command/exec/outputDelta"
                        || !message.TryGetProperty("params", out var parameters)
                        || GetString(parameters, "processId") is not { Length: > 0 } processId
                        || GetString(parameters, "deltaBase64") is not { Length: > 0 } deltaBase64)
                        continue;
                    try
                    {
                        var text = Encoding.UTF8.GetString(Convert.FromBase64String(deltaBase64));
                        OutputReceived?.Invoke(new CodexTerminalOutput(processId, text));
                        if (parameters.TryGetProperty("capReached", out var capReached) && capReached.ValueKind == JsonValueKind.True)
                            OutputReceived?.Invoke(new CodexTerminalOutput(processId, "\r\n[вывод достиг лимита захвата]\r\n"));
                    }
                    catch (FormatException error)
                    {
                        System.Diagnostics.Trace.TraceWarning("Некорректный terminal output chunk: {0}", error.Message);
                    }
                }
                await Task.Delay(25, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Поток терминалов App Server завершился: {0}", error.Message);
        }
    }

    private async Task ObserveSessionAsync(string processId, Task<JsonElement> responseTask)
    {
        var exitCode = -1;
        string? error = null;
        try
        {
            var response = await responseTask;
            if (response.TryGetProperty("exitCode", out var value) && value.TryGetInt32(out var parsedExitCode))
                exitCode = parsedExitCode;
            if (response.TryGetProperty("stderr", out var stderr) && stderr.ValueKind == JsonValueKind.String)
                error = string.IsNullOrWhiteSpace(stderr.GetString()) ? null : stderr.GetString();
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        finally
        {
            _sessions.TryRemove(processId, out _);
            try { SessionExited?.Invoke(new CodexTerminalExit(processId, exitCode, error)); }
            catch (Exception exception) { System.Diagnostics.Trace.TraceWarning("Terminal UI update failed: {0}", exception.Message); }
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _startLock.WaitAsync();
        try
        {
            foreach (var processId in _sessions.Keys.ToArray())
                await TerminateSessionAsync(processId);
            _lifetime.Cancel();
            if (_notificationTask is not null)
            {
                try { await _notificationTask; }
                catch (OperationCanceledException) { }
            }
            if (_connection is not null) await _connection.DisposeAsync();
        }
        finally { _startLock.Release(); }
        _lifetime.Dispose();
    }
}
