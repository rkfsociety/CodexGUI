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
    private const int MaximumCapturedCharactersPerStream = 8 * 1024 * 1024;

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
        var outputTask = ReadBoundedOutputAsync(process.StandardOutput, MaximumCapturedCharactersPerStream);
        var errorTask = ReadBoundedOutputAsync(process.StandardError, MaximumCapturedCharactersPerStream);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }

            // A child process can keep redirected pipes open after the CLI exits.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            throw new TimeoutException("Менеджер плагинов Codex CLI не завершил операцию за две минуты.");
        }
        try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException error)
        {
            throw new TimeoutException("Codex CLI завершился, но не закрыл потоки вывода.", error);
        }
        var output = await outputTask;
        var errors = await errorTask;
        if (output.ExceededLimit || errors.ExceededLimit)
            throw new InvalidDataException($"Codex CLI output exceeded {MaximumCapturedCharactersPerStream} characters per stream.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors.Text) ? output.Text.Trim() : errors.Text.Trim());
        return output.Text;
    }

    internal static async Task<(string Text, bool ExceededLimit)> ReadBoundedOutputAsync(TextReader reader, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

        var output = new StringBuilder(Math.Min(maximumCharacters, 8192));
        var buffer = new char[8192];
        var exceededLimit = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            var remaining = maximumCharacters - output.Length;
            var kept = Math.Min(remaining, count);
            if (kept > 0) output.Append(buffer, 0, kept);
            if (kept < count) exceededLimit = true;
        }

        return (output.ToString(), exceededLimit);
    }

}
