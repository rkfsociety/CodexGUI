using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexGUI;

internal static class CodexAppServerLogin
{
    private sealed record LoginResult(bool Success, string? Error);

    public static async Task<bool> EnsureSignedInAsync()
    {
        await using var connection = await CodexAppServerConnection.StartAsync(CodexAppServerRuntime.ResolveExecutable());
        await connection.InitializeAsync();
        var accountResult = await connection.SendRequestAsync("account/read", new { refreshToken = false });
        var requiresOpenAiAuth = accountResult.TryGetProperty("requiresOpenaiAuth", out var requires)
            && requires.ValueKind == JsonValueKind.True;
        var account = accountResult.TryGetProperty("account", out var accountInfo) ? accountInfo : default;
        if (!requiresOpenAiAuth || account.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            return true;

        var login = await connection.SendRequestAsync("account/login/start", new { type = "chatgptDeviceCode" });
        var loginId = ReadString(login, "loginId");
        var verificationUrl = ReadString(login, "verificationUrl");
        var userCode = ReadString(login, "userCode");
        if (loginId is not { Length: > 0 } || verificationUrl is not { Length: > 0 } || userCode is not { Length: > 0 })
            throw new InvalidDataException("Codex App Server не вернул данные для входа через устройство.");

        var window = new CodexAppServerLoginWindow(verificationUrl, userCode);
        window.Show();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var completionTask = WaitForLoginCompletionAsync(connection, loginId, cancellation.Token);
            var completed = await Task.WhenAny(completionTask, window.Cancelled);
            if (completed != completionTask)
            {
                cancellation.Cancel();
                try { await completionTask; }
                catch (OperationCanceledException) { }
                using var cancelRequestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await connection.SendRequestAsync("account/login/cancel", new { loginId }, cancelRequestTimeout.Token); }
                catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException) { }
                return false;
            }

            var result = await completionTask;
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "Codex sign-in was not completed.");
            return true;
        }
        finally { if (window.IsVisible) window.Close(); }
    }

    private static async Task<LoginResult> WaitForLoginCompletionAsync(CodexAppServerConnection connection,
        string loginId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (connection.TryReadIncoming(out var message))
            {
                if (ReadString(message, "method") != "account/login/completed"
                    || !message.TryGetProperty("params", out var parameters)
                    || ReadString(parameters, "loginId") != loginId)
                    continue;
                var success = parameters.TryGetProperty("success", out var successValue)
                    && successValue.ValueKind == JsonValueKind.True;
                return new LoginResult(success, ReadString(parameters, "error"));
            }

            if (connection.ReaderTask.IsCompleted)
                throw new EndOfStreamException("Codex App Server завершился до завершения входа.");
            await Task.Delay(100, cancellationToken);
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
