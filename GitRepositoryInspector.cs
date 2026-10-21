using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed record RepositoryFileStatus(string Path, char IndexStatus, char WorktreeStatus, bool IsUntracked);

internal sealed record GitRepositorySnapshot(
    bool IsRepository,
    string Root,
    string Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    string? RemoteUrl,
    IReadOnlyList<RepositoryFileStatus> Changes,
    string? Error);

internal static class GitRepositoryInspector
{
    private sealed record GitCommandResult(int ExitCode, string Output, string Error);

    public static async Task<GitRepositorySnapshot> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            return EmptySnapshot(directory, "Папка проекта недоступна.");

        try
        {
            var rootResult = await RunGitAsync(directory, cancellationToken, "rev-parse", "--show-toplevel");
            if (rootResult.ExitCode != 0)
                return EmptySnapshot(directory, "Папка не является Git-репозиторием.");

            var root = rootResult.Output.Trim();
            var statusTask = RunGitAsync(root, cancellationToken, "status", "--porcelain=v2", "--branch", "-z");
            var remoteTask = RunGitAsync(root, cancellationToken, "remote", "get-url", "origin");
            await Task.WhenAll(statusTask, remoteTask);

            var status = await statusTask;
            if (status.ExitCode != 0)
                return EmptySnapshot(root, status.Error.Trim());

            var (branch, upstream, ahead, behind, changes) = ParseStatus(status.Output);
            var remoteResult = await remoteTask;
            var remoteUrl = remoteResult.ExitCode == 0 ? SanitizeRemoteUrl(remoteResult.Output.Trim()) : null;
            return new GitRepositorySnapshot(true, root, branch, upstream, ahead, behind, remoteUrl, changes, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException error)
        {
            return EmptySnapshot(directory, error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException or System.ComponentModel.Win32Exception)
        {
            return EmptySnapshot(directory, error.Message);
        }
    }

    private static GitRepositorySnapshot EmptySnapshot(string path, string error) =>
        new(false, path, "", null, 0, 0, null, [], error);

    private static async Task<GitCommandResult> RunGitAsync(string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = directory
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(directory);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить Git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Git не ответил за 10 секунд.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }

        return new GitCommandResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static (string Branch, string? Upstream, int Ahead, int Behind, IReadOnlyList<RepositoryFileStatus> Changes) ParseStatus(string output)
    {
        var branch = "(detached)";
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var changes = new List<RepositoryFileStatus>();

        var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = record[14..];
                continue;
            }
            if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = record[18..];
                continue;
            }
            if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = record.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    _ = int.TryParse(parts[2].TrimStart('+'), out ahead);
                    _ = int.TryParse(parts[3].TrimStart('-'), out behind);
                }
                continue;
            }

            if (record.StartsWith("? ", StringComparison.Ordinal))
            {
                changes.Add(new RepositoryFileStatus(record[2..], '?', '?', true));
                continue;
            }

            var type = record[0];
            var pathField = type switch { '1' => 8, '2' => 9, 'u' => 10, _ => 0 };
            if (pathField == 0) continue;
            var xy = GetField(record, 1);
            if (xy.Length < 2) continue;
            var path = GetFieldAfter(record, pathField);
            changes.Add(new RepositoryFileStatus(path, xy[0], xy[1], false));
            if (type == '2' && index + 1 < records.Length) index++;
        }

        return (branch, upstream, ahead, behind, changes);
    }

    private static string GetField(string record, int fieldIndex)
    {
        var start = 0;
        for (var field = 0; field < fieldIndex; field++)
        {
            var separator = record.IndexOf(' ', start);
            if (separator < 0) return "";
            start = separator + 1;
        }

        var end = record.IndexOf(' ', start);
        return end < 0 ? record[start..] : record[start..end];
    }

    private static string GetFieldAfter(string record, int fieldIndex)
    {
        var start = 0;
        for (var field = 0; field < fieldIndex; field++)
        {
            var separator = record.IndexOf(' ', start);
            if (separator < 0) return "";
            start = separator + 1;
        }

        return record[start..];
    }

    private static string? SanitizeRemoteUrl(string remote)
    {
        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                var safeUri = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
                return safeUri.Uri.AbsoluteUri.TrimEnd('/');
            }

            if (uri.Scheme == "ssh" && IsPublicGitHost(uri.Host))
                return new UriBuilder(Uri.UriSchemeHttps, uri.Host) { Path = uri.AbsolutePath.TrimEnd('/') }.Uri.AbsoluteUri.TrimEnd('/');
        }

        if (remote.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = remote.IndexOf(':', 4);
            if (colon > 4)
            {
                var host = remote[4..colon];
                if (IsPublicGitHost(host)) return $"https://{host}/{remote[(colon + 1)..].TrimEnd('/') }";
            }
        }

        return null;
    }

    private static bool IsPublicGitHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("bitbucket.org", StringComparison.OrdinalIgnoreCase);
}
