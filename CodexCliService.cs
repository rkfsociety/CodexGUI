using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed record ModelOption(string Slug, string DisplayName, string DefaultReasoningLevel, IReadOnlyList<string> ReasoningLevels, int Priority);

internal static class CodexCliService
{
    public static string ResolveExecutable()
    {
        const string executableName = "codex.exe";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executableName);
            if (File.Exists(candidate)) return candidate;
        }

        var codexBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        var directCandidate = Path.Combine(codexBin, executableName);
        if (File.Exists(directCandidate)) return directCandidate;

        if (Directory.Exists(codexBin))
        {
            var versionedCandidate = Directory.EnumerateDirectories(codexBin)
                .Select(directory => Path.Combine(directory, executableName))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (versionedCandidate is not null) return versionedCandidate;
        }

        throw new FileNotFoundException(
            "Не найден Codex CLI. Установите Codex для Windows или добавьте папку с codex.exe в пользовательский PATH.",
            executableName);
    }

    public static async Task<string> RunPluginCommandAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(ResolveExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("plugin");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить менеджер плагинов Codex CLI.");
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask);
            throw new TimeoutException("Менеджер плагинов Codex CLI не завершил операцию за две минуты.");
        }
        var output = await outputTask;
        var errors = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors) ? output.Trim() : errors.Trim());
        return output;
    }

}
