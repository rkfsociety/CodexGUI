using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed record RepositoryFileStatus(string Path, char IndexStatus, char WorktreeStatus, bool IsUntracked, bool IsConflicted = false);
internal enum RepositoryDiffScope { Unstaged, Staged }
internal enum GitHunkAction { Stage, Unstage, Revert }
internal sealed record RepositoryDiffHunk(int StartLine, int EndLine, string Patch);

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
    string? Error)
{
    public long? UnpushedAddedLines { get; init; }
    public long? UnpushedRemovedLines { get; init; }
    // Exact repository revision at snapshot time, independent of uncommitted changes.
    public string? HeadCommit { get; init; }
}

internal static class GitRepositoryInspector
{
    private sealed record GitCommandResult(int ExitCode, string Output, string Error);

    // A compact, explicitly user-inserted snapshot for the agent's composer.
    // Escape line separators in Git-controlled names so one filename cannot forge extra lines.
    internal static string FormatAgentContext(GitRepositorySnapshot snapshot, int maximumFiles = 40)
    {
        if (!snapshot.IsRepository) throw new ArgumentException("Git snapshot is unavailable.", nameof(snapshot));
        if (maximumFiles < 1) throw new ArgumentOutOfRangeException(nameof(maximumFiles));

        static string OneLine(string value) => EscapeAgentField(value);
        var staged = 0;
        var modified = 0;
        var untracked = 0;
        var conflicted = 0;
        var possibleSecrets = 0;
        var stagedSecrets = 0;
        // A single pass avoids repeatedly scanning large worktrees and secret filenames.
        foreach (var file in snapshot.Changes)
        {
            var isStaged = !file.IsUntracked && file.IndexStatus != '.';
            if (isStaged) staged++;
            if (!file.IsUntracked && file.WorktreeStatus != '.') modified++;
            if (file.IsUntracked) untracked++;
            if (file.IsConflicted) conflicted++;
            if (IsPotentialSecretFile(file.Path))
            {
                possibleSecrets++;
                if (isStaged) stagedSecrets++;
            }
        }
        var result = new StringBuilder();
        result.AppendLine("Контекст локального Git-репозитория (снимок; проверь актуальность перед изменениями):");
        result.Append("Рабочая папка: ").AppendLine(OneLine(snapshot.Root));
        result.Append("Ветка: ").AppendLine(OneLine(snapshot.Branch));
        if (!string.IsNullOrWhiteSpace(snapshot.HeadCommit))
            result.Append("Исходный HEAD-коммит: ").AppendLine(OneLine(snapshot.HeadCommit));
        if (!string.IsNullOrWhiteSpace(snapshot.Upstream))
            result.Append("Upstream: ").AppendLine(OneLine(snapshot.Upstream));
        result.Append("Коммиты относительно upstream: +").Append(snapshot.Ahead)
            .Append(" / -").AppendLine(snapshot.Behind.ToString());
        if (!snapshot.HasHead)
            result.AppendLine("ВНИМАНИЕ: репозиторий без первого коммита (HEAD отсутствует).");
        if (snapshot.Branch == "(detached)")
            result.AppendLine("ВНИМАНИЕ: detached HEAD; коммиты могут оказаться вне ветки.");
        if (string.IsNullOrWhiteSpace(snapshot.Upstream))
            result.AppendLine("ВНИМАНИЕ: upstream не настроен; сравнение с удалённой веткой недоступно.");
        else if (snapshot.Behind > 0)
            result.AppendLine("ВНИМАНИЕ: локальная ветка отстаёт от upstream; проверьте изменения перед отправкой.");
        if (conflicted > 0)
            result.Append("ВНИМАНИЕ: неразрешённых Git-конфликтов: ").Append(conflicted)
                .AppendLine(". Не перезаписывайте конфликтные файлы без проверки.");
        if (possibleSecrets > 0)
        {
            result.Append("ВНИМАНИЕ: потенциально чувствительных файлов: ").Append(possibleSecrets)
                .Append(" (в индексе: ").Append(stagedSecrets)
                .AppendLine("). Проверьте их перед коммитом или отправкой.");
            result.AppendLine("Проверены только имена файлов; их содержимое не анализировалось.");
        }
        result.Append("Файлов с изменениями: ").Append(snapshot.Changes.Count)
            .Append(" (в индексе: ").Append(staged)
            .Append(", изменены: ").Append(modified)
            .Append(", новые: ").Append(untracked).AppendLine(").");
        foreach (var file in snapshot.Changes.Take(maximumFiles))
        {
            var state = file.IsConflicted ? "конфликт"
                : file.IsUntracked ? "новый"
                : file.IndexStatus != '.' && file.WorktreeStatus != '.' ? "индекс + рабочая папка"
                : file.IndexStatus != '.' ? "в индексе" : "в рабочей папке";
            result.Append("- [").Append(state).Append("] ").AppendLine(OneLine(file.Path));
        }
        if (snapshot.Changes.Count > maximumFiles)
            result.Append("… не показано файлов: ").Append(snapshot.Changes.Count - maximumFiles)
                .AppendLine(". Получите полный список локально.");
        return result.ToString().TrimEnd();
    }


    // Git can represent terminal control and bidirectional formatting characters in filenames.
    // Never allow those characters to alter the structure of the context inserted into a prompt.
    internal static string EscapeAgentField(string value)
    {
        StringBuilder? escaped = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character) ||
                char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
            {
                escaped ??= new StringBuilder(value.Length + 8).Append(value.AsSpan(0, index));
                switch (character)
                {
                    case '\r': escaped.Append("\\r"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default: escaped.Append("\\u").Append(((int)character).ToString("X4")); break;
                }
            }
            else
            {
                escaped?.Append(character);
            }
        }
        return escaped?.ToString() ?? value;
    }

    private static readonly string[] SafeEnvTemplateSuffixes =
        [".example", ".sample", ".template", ".dist", ".default"];

    // Filename-only preflight: do not open files or inject their secrets into agent context.
    internal static bool IsPotentialSecretFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        var name = path[(separator + 1)..];
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var suffix in SafeEnvTemplateSuffixes)
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        return name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".pypirc", StringComparison.OrdinalIgnoreCase)
            || name.Equals("credentials.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("service-account.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("service_account.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_ecdsa", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".p12", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tfvars", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tfvars.json", StringComparison.OrdinalIgnoreCase);
    }

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
            var statusTask = RunGitAsync(root, cancellationToken, "status", "--porcelain=v2", "--branch", "--untracked-files=all", "-z");
            var remoteTask = RunGitAsync(root, cancellationToken, "remote", "get-url", "origin");
            await Task.WhenAll(statusTask, remoteTask);

            var status = await statusTask;
            if (status.ExitCode != 0)
                return EmptySnapshot(root, string.IsNullOrWhiteSpace(status.Error)
                    ? "Не удалось прочитать Git status."
                    : status.Error.Trim());

            var (branch, upstream, ahead, behind, changes, hasHead, headCommit) = ParseStatus(status.Output);
            var remoteResult = await remoteTask;
            var remoteUrl = remoteResult.ExitCode == 0 ? SanitizeRemoteUrl(remoteResult.Output.Trim()) : null;
            var comparisonBase = await ResolveUnpushedComparisonBaseAsync(root, upstream, hasHead,
                remoteResult.ExitCode == 0, cancellationToken);
            long? addedLines = null;
            long? removedLines = null;
            if (comparisonBase is not null)
            {
                var lineCounts = await ReadUnpushedLineCountsAsync(root, changes, comparisonBase, cancellationToken);
                if (lineCounts is { } counts)
                {
                    addedLines = counts.Added;
                    removedLines = counts.Removed;
                }
            }
            return new GitRepositorySnapshot(true, root, branch, upstream, ahead, behind, remoteUrl,
                remoteResult.ExitCode == 0, hasHead, changes, null)
            {
                UnpushedAddedLines = addedLines,
                UnpushedRemovedLines = removedLines,
                HeadCommit = headCommit
            };
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


    private static async Task<string?> ResolveUnpushedComparisonBaseAsync(
        string root, string? upstream, bool hasHead, bool hasOrigin, CancellationToken cancellationToken)
    {
        var comparisonRef = upstream;
        if (string.IsNullOrWhiteSpace(comparisonRef) && hasOrigin)
        {
            var remoteHead = await RunGitAsync(root, cancellationToken, "rev-parse", "--verify", "--end-of-options", "refs/remotes/origin/HEAD");
            if (remoteHead.ExitCode == 0)
                comparisonRef = remoteHead.Output.Trim();
        }
        if (string.IsNullOrWhiteSpace(comparisonRef)) return null;

        var resolved = await RunGitAsync(root, cancellationToken, "rev-parse", "--verify", "--end-of-options", comparisonRef);
        if (resolved.ExitCode != 0 || !hasHead) return resolved.ExitCode == 0 ? resolved.Output.Trim() : null;

        var mergeBase = await RunGitAsync(root, cancellationToken, "merge-base", resolved.Output.Trim(), "HEAD");
        return mergeBase.ExitCode == 0 && !string.IsNullOrWhiteSpace(mergeBase.Output)
            ? mergeBase.Output.Trim()
            : null;
    }

    // Compare the complete working tree with the last common remote commit. This includes local
    // commits and staged/unstaged edits without double-counting; untracked text files are added below.
    private static async Task<(long Added, long Removed)?> ReadUnpushedLineCountsAsync(
        string root, IReadOnlyList<RepositoryFileStatus> changes, string comparisonBase, CancellationToken cancellationToken)
    {
        var diff = await RunGitAsync(root, cancellationToken, "diff", "--no-ext-diff", "--no-textconv", "--numstat", "-z", comparisonBase);
        if (diff.ExitCode != 0) return null;

        var (added, removed) = ParseNumstat(diff.Output);
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        // Bound disk activity for projects with many untracked files; do not block the UI on serial reads.
        await Parallel.ForEachAsync(changes.Where(file => file.IsUntracked),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                try
                {
                    var fullPath = Path.GetFullPath(Path.Combine(root, file.Path));
                    if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return;
                    var lineCount = await CountTextLinesAsync(fullPath, token);
                    if (lineCount is { } count) Interlocked.Add(ref added, count);
                }
                catch (IOException) { } // Untracked files can disappear during refresh.
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            });
        return (added, removed);
    }

    // Git -z separates records with NULs, not newlines. A rename has a blank path field
    // followed by two standalone NUL-delimited names that must not be counted as numstat.
    internal static (long Added, long Removed) ParseNumstat(string output)
    {
        long added = 0;
        long removed = 0;
        var remaining = output.AsSpan();
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOf('\0');
            var record = end >= 0 ? remaining[..end] : remaining;
            remaining = end >= 0 ? remaining[(end + 1)..] : ReadOnlySpan<char>.Empty;

            var firstTab = record.IndexOf('\t');
            if (firstTab < 0) continue;
            var nextTab = record[(firstTab + 1)..].IndexOf('\t');
            if (nextTab < 0) continue;
            var secondTab = firstTab + 1 + nextTab;
            if (long.TryParse(record[..firstTab], out var insertions)) added += insertions;
            if (long.TryParse(record[(firstTab + 1)..secondTab], out var deletions)) removed += deletions;

            if (secondTab != record.Length - 1) continue;
            // Skip the two paths of a rename/copy, even if they contain tabs or numbers.
            for (var index = 0; index < 2 && !remaining.IsEmpty; index++)
            {
                var pathEnd = remaining.IndexOf('\0');
                remaining = pathEnd >= 0 ? remaining[(pathEnd + 1)..] : ReadOnlySpan<char>.Empty;
            }
        }
        return (added, removed);
    }

    // Count a bounded file in small chunks rather than allocating its entire contents per refresh.
    // Preserve the previous behavior: binary (NUL-containing) files contribute no line count.
    internal static async Task<long?> CountTextLinesAsync(string path, CancellationToken cancellationToken)
    {
        const long maximumBytes = 4L * 1024 * 1024;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > maximumBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return null;

        var buffer = new byte[16 * 1024];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long lines = 0;
        long bytesRead = 0;
        byte lastByte = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            bytesRead += count;
            if (bytesRead > maximumBytes || buffer.AsSpan(0, count).Contains((byte)0)) return null;
            for (var index = 0; index < count; index++)
                if (buffer[index] == (byte)'\n') lines++;
            lastByte = buffer[count - 1];
        }

        return lines + (bytesRead > 0 && lastByte != (byte)'\n' ? 1 : 0);
    }

    public static async Task<string> ReadFileDiffAsync(string repositoryRoot, RepositoryFileStatus file,
        RepositoryDiffScope scope, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, file.Path));
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь файла выходит за пределы репозитория.");
        var gitPath = ValidateRepositoryRelativePath(root, file.Path);

        GitCommandResult result;
        if (file.IsUntracked)
        {
            if (scope == RepositoryDiffScope.Staged) return "";
            result = await RunGitAsync(root, cancellationToken,
                "diff", "--no-index", "--no-ext-diff", "--no-color", "--", "/dev/null", fullPath);
        }
        else
        {
            var arguments = new List<string> { "diff", "--no-ext-diff", "--no-color" };
            if (scope == RepositoryDiffScope.Staged) arguments.Add("--cached");
            arguments.Add("--");
            arguments.Add(gitPath);
            result = await RunGitAsync(root, cancellationToken, arguments.ToArray());
        }
        if (result.ExitCode != 0 && !(file.IsUntracked && result.ExitCode == 1))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim());

        const int maximumDiffCharacters = 500_000;
        return result.Output.Length <= maximumDiffCharacters
            ? result.Output
            : result.Output[..maximumDiffCharacters] + "\n\n… Diff сокращён: вывод превышает 500 000 символов.";
    }

    public static IReadOnlyList<RepositoryDiffHunk> ParseDiffHunks(string diff)
    {
        if (string.IsNullOrEmpty(diff)) return [];
        var lines = diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var firstHunk = Array.FindIndex(lines, line => line.StartsWith("@@", StringComparison.Ordinal));
        if (firstHunk < 0 || !lines.Take(firstHunk).Any(line => line.StartsWith("diff --git ", StringComparison.Ordinal)))
            return [];

        var header = lines.Take(firstHunk).ToArray();
        var hunks = new List<RepositoryDiffHunk>();
        for (var start = firstHunk; start < lines.Length;)
        {
            if (!lines[start].StartsWith("@@", StringComparison.Ordinal)) { start++; continue; }
            var end = start + 1;
            while (end < lines.Length
                && !lines[end].StartsWith("@@", StringComparison.Ordinal)
                && !lines[end].StartsWith("diff --git ", StringComparison.Ordinal)) end++;

            var hasChange = lines.Skip(start + 1).Take(end - start - 1).Any(line =>
                line.StartsWith('+') || line.StartsWith('-'));
            if (hasChange)
            {
                var patchLines = header.Concat(lines.Skip(start).Take(end - start));
                var patch = string.Join('\n', patchLines).TrimEnd('\n') + "\n";
                hunks.Add(new RepositoryDiffHunk(start, end - 1, patch));
            }
            start = end;
        }
        return hunks;
    }

    public static async Task ApplyDiffHunkAsync(string repositoryRoot, string relativePath, string patch,
        GitHunkAction action, CancellationToken cancellationToken)
    {
        _ = ValidateRepositoryRelativePath(repositoryRoot, relativePath);
        if (string.IsNullOrWhiteSpace(patch) || !patch.StartsWith("diff --git ", StringComparison.Ordinal))
            throw new InvalidDataException("Выбранный diff-hunk не содержит заголовок Git patch.");

        var root = Path.GetFullPath(repositoryRoot);
        var patchPath = Path.Combine(Path.GetTempPath(), $"CodexGUI-hunk-{Guid.NewGuid():N}.patch");
        try
        {
            await File.WriteAllTextAsync(patchPath, patch, new System.Text.UTF8Encoding(false), cancellationToken);
            var arguments = action switch
            {
                GitHunkAction.Stage => new[] { "apply", "--cached", "--", patchPath },
                GitHunkAction.Unstage => new[] { "apply", "--cached", "--reverse", "--", patchPath },
                GitHunkAction.Revert => new[] { "apply", "--reverse", "--", patchPath },
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
            var result = await RunGitAsync(root, cancellationToken, arguments);
            EnsureGitCommandSucceeded(result, "Не удалось применить действие к diff-hunk.");
        }
        finally
        {
            try { File.Delete(patchPath); }
            catch (IOException error) { System.Diagnostics.Trace.TraceWarning("Не удалось удалить временный Git patch {0}: {1}", patchPath, error.Message); }
            catch (UnauthorizedAccessException error) { System.Diagnostics.Trace.TraceWarning("Не удалось удалить временный Git patch {0}: {1}", patchPath, error.Message); }
        }
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

    public static async Task<string> PreviewUntrackedRemovalAsync(string repositoryRoot, string relativePath,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var path = ValidateRepositoryRelativePath(root, relativePath);
        var result = await RunGitAsync(root, cancellationToken, TimeSpan.FromMinutes(2),
            "clean", "--dry-run", "-d", "-f", "--", path);
        EnsureGitCommandSucceeded(result, "Не удалось проверить неотслеживаемые файлы.");
        return result.Output.Trim();
    }

    public static async Task RemoveUntrackedPathAsync(string repositoryRoot, string relativePath, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var path = ValidateRepositoryRelativePath(root, relativePath);
        var result = await RunGitAsync(root, cancellationToken, TimeSpan.FromMinutes(5),
            "clean", "--quiet", "-d", "-f", "--", path);
        EnsureGitCommandSucceeded(result, "Не удалось удалить неотслеживаемые файлы.");
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
        var branchResult = await RunGitAsync(root, cancellationToken, "symbolic-ref", "--quiet", "--short", "HEAD");
        var branch = branchResult.Output.Trim();
        if (branchResult.ExitCode != 0 || branch.Length == 0)
            throw new InvalidOperationException("Нельзя отправить detached HEAD: переключитесь на ветку.");

        GitCommandResult result;
        if (setUpstream)
            result = await RunGitAsync(root, cancellationToken, TimeSpan.FromMinutes(5),
                "push", "--set-upstream", "origin", "HEAD");
        else
        {
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
            await TerminateGitProcessAsync(process);
            throw new TimeoutException($"Git не ответил за {timeout.TotalSeconds:0} секунд.");
        }
        catch (OperationCanceledException)
        {
            await TerminateGitProcessAsync(process);
            throw;
        }

        // Git may exit while a helper still owns the redirected handles.
        try
        {
            await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException("Git завершился, но его потоки вывода остались открыты.", error);
        }
        return new GitCommandResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static async Task TerminateGitProcessAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        // Do not hide the original cancellation/timeout when a terminated child lingers.
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
    }

    internal static (string Branch, string? Upstream, int Ahead, int Behind,
        IReadOnlyList<RepositoryFileStatus> Changes, bool HasHead, string? HeadCommit) ParseStatus(string output)
    {
        var branch = "(detached)";
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var hasHead = true;
        string? headCommit = null;
        var changes = new List<RepositoryFileStatus>();

        // Parse porcelain v2 records in-place. Split('\0') allocates a large array and
        // a string for every record, even for filenames we only need to read once.
        var remaining = output.AsSpan();
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOf('\0');
            var record = end >= 0 ? remaining[..end] : remaining;
            remaining = end >= 0 ? remaining[(end + 1)..] : ReadOnlySpan<char>.Empty;
            if (record.IsEmpty) continue;

            if (record.StartsWith("# branch.head ".AsSpan(), StringComparison.Ordinal))
            {
                branch = record[14..].ToString();
                continue;
            }
            if (record.StartsWith("# branch.oid ".AsSpan(), StringComparison.Ordinal))
            {
                var objectId = record[13..];
                hasHead = !objectId.SequenceEqual("(initial)".AsSpan());
                headCommit = hasHead && IsGitObjectId(objectId) ? objectId.ToString() : null;
                continue;
            }
            if (record.StartsWith("# branch.upstream ".AsSpan(), StringComparison.Ordinal))
            {
                upstream = record[18..].ToString();
                continue;
            }
            if (record.StartsWith("# branch.ab ".AsSpan(), StringComparison.Ordinal))
            {
                var counts = record[12..];
                var separator = counts.IndexOf(' ');
                if (separator > 0 &&
                    int.TryParse(counts[..separator].TrimStart('+'), out var parsedAhead) &&
                    int.TryParse(counts[(separator + 1)..].TrimStart('-'), out var parsedBehind))
                {
                    ahead = parsedAhead;
                    behind = parsedBehind;
                }
                continue;
            }

            if (record.StartsWith("? ".AsSpan(), StringComparison.Ordinal))
            {
                changes.Add(new RepositoryFileStatus(record[2..].ToString(), '?', '?', true));
                continue;
            }

            var type = record[0];
            var pathField = type switch { '1' => 8, '2' => 9, 'u' => 10, _ => 0 };
            if (pathField == 0) continue;
            var xy = GetField(record, 1);
            if (xy.Length < 2) continue;
            var path = GetFieldAfter(record, pathField);
            changes.Add(new RepositoryFileStatus(path.ToString(), xy[0], xy[1], false, type == 'u'));
            if (type == '2')
            {
                // The following NUL-delimited field is the original rename/copy path.
                var renameEnd = remaining.IndexOf('\0');
                remaining = renameEnd >= 0 ? remaining[(renameEnd + 1)..] : ReadOnlySpan<char>.Empty;
            }
        }

        return (branch, upstream, ahead, behind, changes, hasHead, headCommit);
    }

    private static bool IsGitObjectId(ReadOnlySpan<char> objectId)
    {
        if (objectId.Length is not (40 or 64)) return false;
        foreach (var character in objectId)
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
                return false;
        return true;
    }

    private static ReadOnlySpan<char> GetField(ReadOnlySpan<char> record, int fieldIndex)
    {
        var value = GetFieldAfter(record, fieldIndex);
        var end = value.IndexOf(' ');
        return end < 0 ? value : value[..end];
    }

    private static ReadOnlySpan<char> GetFieldAfter(ReadOnlySpan<char> record, int fieldIndex)
    {
        var start = 0;
        for (var field = 0; field < fieldIndex; field++)
        {
            var separator = record[start..].IndexOf(' ');
            if (separator < 0) return ReadOnlySpan<char>.Empty;
            start += separator + 1;
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
