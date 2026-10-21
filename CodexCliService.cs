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

    public static async Task RunSessionCommandAsync(string command, string sessionId)
    {
        var startInfo = new ProcessStartInfo(ResolveExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(command);
        if (command == "delete") startInfo.ArgumentList.Add("--force");
        startInfo.ArgumentList.Add(sessionId);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить codex CLI.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var errors = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors) ? output.Trim() : errors.Trim());
    }

    public static async Task<string> ReviewUncommittedChangesAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ResolveExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("review");
        startInfo.ArgumentList.Add("--uncommitted");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить проверку изменений Codex.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask);
            throw;
        }

        var output = await outputTask;
        var errors = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors) ? output.Trim() : errors.Trim());
        return output.Trim();
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

    public static async Task<IReadOnlyList<ModelOption>> LoadModelCatalogAsync(string defaultEffort)
    {
        var startInfo = new ProcessStartInfo(ResolveExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("debug");
        startInfo.ArgumentList.Add("models");
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить codex debug models.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var errorOutput = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorOutput)
                ? $"codex debug models завершился с кодом {process.ExitCode}."
                : errorOutput.Trim());

        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("models", out var models))
            throw new InvalidDataException("Ответ Codex CLI не содержит каталог моделей.");

        return models.EnumerateArray()
            .Where(item => GetString(item, "visibility") == "list" && item.TryGetProperty("supported_in_api", out var supported) && supported.GetBoolean())
            .Select(item => new ModelOption(
                GetString(item, "slug") ?? "",
                GetString(item, "display_name") ?? GetString(item, "slug") ?? "",
                GetString(item, "default_reasoning_level") ?? defaultEffort,
                item.TryGetProperty("supported_reasoning_levels", out var efforts)
                    ? efforts.EnumerateArray().Select(effort => GetString(effort, "effort")).Where(effort => effort is not null).Cast<string>().ToArray()
                    : [],
                item.TryGetProperty("priority", out var priority) && priority.TryGetInt32(out var order) ? order : int.MaxValue))
            .Where(model => !string.IsNullOrWhiteSpace(model.Slug) && model.ReasoningLevels.Count > 0)
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
