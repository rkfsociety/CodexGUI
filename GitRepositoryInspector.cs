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
    bool HasOrigin,
    bool HasHead,
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

            var (branch, upstream, ahead, behind, changes, hasHead) = ParseStatus(status.Output);
            var remoteResult = await remoteTask;
            var remoteUrl = remoteResult.ExitCode == 0 ? SanitizeRemoteUrl(remoteResult.Output.Trim()) : null;
            return new GitRepositorySnapshot(true, root, branch, upstream, ahead, behind, remoteUrl,
                remoteResult.ExitCode == 0, hasHead, changes, null);
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

    public static async Task<string> ReadFileDiffAsync(string repositoryRoot, RepositoryFileStatus file, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, file.Path));
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь файла выходит за пределы репозитория.");

        var result = file.IsUntracked
            ? await RunGitAsync(root, cancellationToken, "diff", "--no-index", "--no-ext-diff", "--no-color", "--", "/dev/null", fullPath)
            : await RunGitAsync(root, cancellationToken, "diff", "--no-ext-diff", "--no-color", "HEAD", "--", file.Path);
        if (result.ExitCode > 1)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim());

        const int maximumDiffCharacters = 500_000;
        return result.Output.Length <= maximumDiffCharacters
            ? result.Output
            : result.Output[..maximumDiffCharacters] + "\n\n… Diff сокращён: вывод превышает 500 000 символов.";
    }

    public static Task StageFileAsync(string repositoryRoot, string relativePath, CancellationToken cancellationToken) =>
        RunGitMutationAsync(repositoryRoot, cancellationToken, "add", "--", ValidateRepositoryRelativePath(repositoryRoot, relativePath));

    public static async Task UnstageFileAsync(string repositoryRoot, string relativePath, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var path = ValidateRepositoryRelativePath(root, relativePath);
        var hasHead = await HasHeadAsync(root, cancellationToken);
        var result = hasHead
            ? await RunGitAsync(root, cancellationToken, "restore", "--staged", "--", path)
            : await RunGitAsync(root, cancellationToken, "rm", "--cached", "--force", "--ignore-unmatch", "--", path);
        EnsureGitCommandSucceeded(result, "Не удалось убрать файл из индекса.");
    }

    public static Task RestoreFileFromHeadAsync(string repositoryRoot, string relativePath, CancellationToken cancellationToken) =>
        RunGitMutationAsync(repositoryRoot, cancellationToken, "restore", "--source=HEAD", "--staged", "--worktree", "--",
            ValidateRepositoryRelativePath(repositoryRoot, relativePath));

    public static Task StageAllAsync(string repositoryRoot, CancellationToken cancellationToken) =>
        RunGitMutationAsync(repositoryRoot, cancellationToken, "add", "--all", "--", ".");

    public static async Task UnstageAllAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var hasHead = await HasHeadAsync(root, cancellationToken);
        var result = hasHead
            ? await RunGitAsync(root, cancellationToken, "restore", "--staged", "--", ".")
            : await RunGitAsync(root, cancellationToken, "rm", "--cached", "-r", "--force", "--ignore-unmatch", "--", ".");
        EnsureGitCommandSucceeded(result, "Не удалось убрать изменения из индекса.");
    }

    public static async Task<string> CommitStagedAsync(string repositoryRoot, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Укажите сообщение коммита.", nameof(message));

        var root = Path.GetFullPath(repositoryRoot);
        var result = await RunGitAsync(root, cancellationToken, TimeSpan.FromMinutes(2), "commit", "-m", message.Trim());
        EnsureGitCommandSucceeded(result, "Не удалось создать коммит.");
        return result.Output.Trim();
    }

    public static async Task PushCurrentBranchAsync(string repositoryRoot, bool setUpstream, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        GitCommandResult result;
        if (setUpstream)
            result = await RunGitAsync(root, cancellationToken, TimeSpan.FromMinutes(5),
                "push", "--set-upstream", "origin", "HEAD");
        else
        {
            var branchResult = await RunGitAsync(root, cancellationToken, "symbolic-ref", "--quiet", "--short", "HEAD");
            var branch = branchResult.Output.Trim();
            if (branchResult.ExitCode != 0 || branch.Length == 0)
                throw new InvalidOperationException("Нельзя отправить detached HEAD: переключитесь на ветку.");

            var remoteResult = await RunGitAsync(root, cancellationToken, "config", "--get", $"branch.{branch}.remote");
            var mergeResult = await RunGitAsync(root, cancellationToken, "config", "--get", $"branch.{branch}.merge");
            var remote = remoteResult.Output.Trim();
            var mergeRef = mergeResult.Output.Trim();
            if (remoteResult.ExitCode != 0 || remote.Length == 0
                || mergeResult.ExitCode != 0 || !mergeRef.StartsWith("refs/heads/", StringComparison.Ordinal))
                throw new InvalidDataException("Не удалось определить upstream текущей ветки.");

            result = await RunGitAsync(root, cancellationToken, TimeSpan.FromMinutes(5),
                "push", remote, $"HEAD:{mergeRef}");
        }
        EnsureGitCommandSucceeded(result, "Не удалось отправить ветку в удалённый репозиторий.");
    }

    public static async Task<string> CreateDetachedWorktreeAsync(string repositoryPath, string worktreePath,
        CancellationToken cancellationToken)
    {
        var repository = await ReadAsync(repositoryPath, cancellationToken);
        if (!repository.IsRepository)
            throw new InvalidOperationException(repository.Error ?? "Папка проекта не является Git-репозиторием.");

        var root = Path.GetFullPath(repository.Root);
        var destination = Path.GetFullPath(worktreePath);
        var managedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "worktrees"));
        var managedPrefix = Path.TrimEndingDirectorySeparator(managedRoot) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(managedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CodexGUI worktree должен находиться в локальной папке приложения.");
        var rootInfo = new DirectoryInfo(managedRoot);
        if (rootInfo.Exists && (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Папка worktrees не должна быть junction или symbolic link.");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Папка для изолированной копии уже существует.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var result = await RunGitAsync(root, cancellationToken, "worktree", "add", "--detach", destination, "HEAD");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? result.Output.Trim()
                : result.Error.Trim());

        if (!await IsRegisteredWorktreeAsync(root, destination, cancellationToken))
            throw new InvalidDataException("Git не подтвердил созданный worktree.");
        return destination;
    }

    public static async Task<bool> IsRegisteredWorktreeAsync(string repositoryPath, string worktreePath,
        CancellationToken cancellationToken)
    {
        var repository = await ReadAsync(repositoryPath, cancellationToken);
        if (!repository.IsRepository) return false;
        var projectCommonDirectory = await ReadGitPathAsync(repository.Root, "--git-common-dir", cancellationToken);
        var worktreeCommonDirectory = await ReadGitPathAsync(worktreePath, "--git-common-dir", cancellationToken);
        var worktreeGitDirectory = await ReadGitPathAsync(worktreePath, "--git-dir", cancellationToken);
        return projectCommonDirectory is not null
            && worktreeCommonDirectory is not null
            && worktreeGitDirectory is not null
            && PathsEqual(projectCommonDirectory, worktreeCommonDirectory)
            && !PathsEqual(worktreeGitDirectory, worktreeCommonDirectory);
    }

    private static async Task<string?> ReadGitPathAsync(string directory, string pathKind, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(directory, cancellationToken, "rev-parse", "--path-format=absolute", pathKind);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output)
            ? Path.GetFullPath(result.Output.Trim())
            : null;
    }

    private static GitRepositorySnapshot EmptySnapshot(string path, string error) =>
        new(false, path, "", null, 0, 0, null, false, false, [], error);

    private static string ValidateRepositoryRelativePath(string repositoryRoot, string relativePath)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Git-путь должен быть относительным путём внутри репозитория.");

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Git-путь выходит за пределы репозитория.");
        var gitPath = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        return $":(literal){gitPath}";
    }

    private static async Task RunGitMutationAsync(string repositoryRoot, CancellationToken cancellationToken, params string[] arguments)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var result = await RunGitAsync(root, cancellationToken, arguments);
        EnsureGitCommandSucceeded(result, "Не удалось выполнить Git-действие.");
    }

    private static void EnsureGitCommandSucceeded(GitCommandResult result, string fallbackMessage)
    {
        if (result.ExitCode == 0) return;
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? fallbackMessage : detail);
    }

    private static async Task<bool> HasHeadAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repositoryRoot, cancellationToken, "rev-parse", "--verify", "HEAD");
        return result.ExitCode == 0;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static Task<GitCommandResult> RunGitAsync(string directory, CancellationToken cancellationToken, params string[] arguments) =>
        RunGitAsync(directory, cancellationToken, TimeSpan.FromSeconds(10), arguments);

    private static async Task<GitCommandResult> RunGitAsync(string directory, CancellationToken cancellationToken,
        TimeSpan timeout, params string[] arguments)
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
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Git не ответил за {timeout.TotalSeconds:0} секунд.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }

        return new GitCommandResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static (string Branch, string? Upstream, int Ahead, int Behind,
        IReadOnlyList<RepositoryFileStatus> Changes, bool HasHead) ParseStatus(string output)
    {
        var branch = "(detached)";
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var hasHead = true;
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
            if (record.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                hasHead = !record.EndsWith("(initial)", StringComparison.Ordinal);
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

        return (branch, upstream, ahead, behind, changes, hasHead);
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
