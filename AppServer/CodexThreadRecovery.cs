using System;

namespace CodexGUI;

/// <summary>Only missing-session errors permit automatic migration to a fresh chat.</summary>
internal static class CodexThreadRecovery
{
    internal static bool IsMissingThreadError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        return message.Contains("thread not loaded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("thread not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unknown thread", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no such thread", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no rollout found for thread id", StringComparison.OrdinalIgnoreCase)
            || message.Contains("thread does not exist", StringComparison.OrdinalIgnoreCase)
            || message.Contains("session not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("чат не найден", StringComparison.OrdinalIgnoreCase)
            || message.Contains("сессия не найдена", StringComparison.OrdinalIgnoreCase);
    }
}
