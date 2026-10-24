using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed record CodexTerminalOutput(string ProcessId, string Text);
internal sealed record CodexTerminalExit(string ProcessId, int ExitCode, string? Error);

internal sealed class CodexAppServerTerminalHost : IAsyncDisposable
{
    private static readonly TimeSpan DefaultControlRequestTimeout = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly CancellationToken _startupToken;
    private readonly Func<Task<CodexAppServerConnection>> _connect;
    private readonly TimeSpan _controlRequestTimeout;
    private CodexAppServerConnection? _connection;
    private Task? _notificationTask;
    private int _disposed;

    public event Action<CodexTerminalOutput>? OutputReceived;
    public event Action<CodexTerminalExit>? SessionExited;

    internal CodexAppServerTerminalHost()
        : this(() => CodexAppServerConnection.StartAsync(CodexAppServerRuntime.ResolveExecutable())) { }

    internal CodexAppServerTerminalHost(
        Func<Task<CodexAppServerConnection>> connect,
        TimeSpan? controlRequestTimeout = null)
    {
        _lifetimeToken = _lifetime.Token;
        _startupToken = _startupCancellation.Token;
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _controlRequestTimeout = controlRequestTimeout ?? DefaultControlRequestTimeout;
        if (_controlRequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(controlRequestTimeout));
    }

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
        await SendControlRequestAsync(connection, "command/exec/write", new
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
        await SendControlRequestAsync(connection, "command/exec/resize", new
        {
            processId,
            size = new { rows = Math.Clamp(rows, 8, 120), cols = Math.Clamp(cols, 30, 300) }
        });
    }

    public Task<bool> TerminateSessionAsync(string processId) =>
        TryTerminateSessionAsync(processId, CancellationToken.None);

    private async Task<bool> TryTerminateSessionAsync(string processId, CancellationToken cancellationToken)
    {
        if (!_sessions.ContainsKey(processId)) return true;
        if (_connection is not { } connection) return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        timeout.CancelAfter(_controlRequestTimeout);
        try
        {
            await connection.SendRequestAsync("command/exec/terminate", new { processId }, timeout.Token);
            return true;
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning("Истекло ожидание завершения терминала {0}: {1}", processId, error.Message);
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            System.Diagnostics.Trace.TraceWarning("Не удалось завершить терминал {0}: {1}", processId, error.Message);
            return false;
        }
    }

    private async Task<JsonElement> SendControlRequestAsync(
        CodexAppServerConnection connection,
        string method,
        object parameters)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        timeout.CancelAfter(_controlRequestTimeout);
        try
        {
            return await connection.SendRequestAsync(method, parameters, timeout.Token);
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"Terminal App Server RPC {method} did not respond within {_controlRequestTimeout.TotalSeconds:0} seconds.", error);
        }
    }

    private async Task EnsureConnectedAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CodexAppServerTerminalHost));
        if (_connection is not null) return;
        var cancellationToken = _startupToken;
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CodexAppServerTerminalHost));
            if (_connection is not null) return;
            var connection = await _connect();
            try { await connection.InitializeAsync(cancellationToken); }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
            _connection = connection;
            _notificationTask = Task.Run(() => ReadNotificationsAsync(connection, _lifetimeToken));
        }
        finally { _startLock.Release(); }
    }

    private async Task ReadNotificationsAsync(CodexAppServerConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (await connection.WaitForIncomingAsync(cancellationToken))
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
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ChannelClosedException error) when (error.InnerException is null) { }
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
        _startupCancellation.Cancel();
        await _startLock.WaitAsync();
        try
        {
            try
            {
                using var terminationDeadline = new CancellationTokenSource(_controlRequestTimeout);
                var terminations = _sessions.Keys.ToArray()
                    .Select(processId => TryTerminateSessionAsync(processId, terminationDeadline.Token));
                await Task.WhenAll(terminations);
            }
            finally
            {
                _lifetime.Cancel();
                if (_notificationTask is not null)
                {
                    try { await _notificationTask; }
                    catch (OperationCanceledException) { }
                }
                if (_connection is not null) await _connection.DisposeAsync();
            }
        }
        finally
        {
            _startLock.Release();
            _startupCancellation.Dispose();
            _lifetime.Dispose();
        }
    }
}
