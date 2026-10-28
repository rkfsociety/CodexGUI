using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexGUI;

internal static class GitHubPullRequestService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private const int MaximumCapturedCharactersPerStream = 1024 * 1024;

    public static bool TryGetRepositorySlug(string? remoteUrl, out string repositorySlug)
    {
        repositorySlug = "";
        if (string.IsNullOrWhiteSpace(remoteUrl)) return false;

        string path;
        if (remoteUrl.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = remoteUrl["git@github.com:".Length..].Trim('/');
        }
        else
        {
            if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
                || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme is not ("https" or "http" or "ssh" or "git"))
                return false;
            path = uri.AbsolutePath.Trim('/');
        }

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts.All(IsRepositorySegment)) return false;
        repositorySlug = parts[0] + "/" + parts[1];
        return true;
    }

    private static bool IsRepositorySegment(string segment) =>
        segment.Length > 0 && segment is not ("." or "..")
        && segment.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');

    public static async Task<string> CreateAsync(string repositoryRoot, string repositorySlug,
        string branch, string title, string body, bool draft, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("gh")
        {
            WorkingDirectory = Path.GetFullPath(repositoryRoot),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        startInfo.ArgumentList.Add("pr");
        startInfo.ArgumentList.Add("create");
        startInfo.ArgumentList.Add("--repo");
        startInfo.ArgumentList.Add(repositorySlug);
        startInfo.ArgumentList.Add("--head");
        startInfo.ArgumentList.Add(branch);
        startInfo.ArgumentList.Add("--title");
        startInfo.ArgumentList.Add(title);
        startInfo.ArgumentList.Add("--body");
        startInfo.ArgumentList.Add(body);
        if (draft) startInfo.ArgumentList.Add("--draft");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить GitHub CLI (gh).");
        // Drain both pipes without retaining unbounded output from a stuck or noisy gh process.
        var outputTask = CodexCliService.ReadBoundedOutputAsync(process.StandardOutput, MaximumCapturedCharactersPerStream);
        var errorTask = CodexCliService.ReadBoundedOutputAsync(process.StandardError, MaximumCapturedCharactersPerStream);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(CommandTimeout);
        }
        catch (TimeoutException)
        {
            await TerminateProcessAsync(process);
            throw new TimeoutException("GitHub CLI не завершил создание Pull Request за 120 секунд.");
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessAsync(process);
            throw;
        }

        try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException timeoutError)
        {
            throw new TimeoutException("GitHub CLI завершился, но не закрыл потоки вывода.", timeoutError);
        }
        return ParseCreateOutput(await outputTask, await errorTask, process.ExitCode, repositorySlug);
    }

    internal static string ParseCreateOutput(
        (string Text, bool ExceededLimit) output, (string Text, bool ExceededLimit) error,
        int exitCode, string repositorySlug)
    {
        if (output.ExceededLimit || error.ExceededLimit)
            throw new InvalidDataException("Вывод GitHub CLI превысил допустимый размер 1 МиБ на поток.");

        if (exitCode != 0)
        {
            var detail = string.Join(Environment.NewLine,
                new[] { error.Text.Trim(), output.Text.Trim() }.Where(value => value.Length > 0));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "GitHub CLI не смог создать Pull Request. Проверьте gh auth status."
                : detail);
        }

        var pullRequestUrl = output.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => IsPullRequestUrl(line, repositorySlug));
        return pullRequestUrl ?? throw new InvalidDataException("GitHub CLI сообщил об успехе, но не вернул ссылку на Pull Request.");
    }

    // gh normally prints a canonical PR URL. Do not mistake an unrelated or malformed
    // GitHub link in CLI output for a successfully created pull request.
    internal static bool IsPullRequestUrl(string value, string repositorySlug)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
            return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length == 4
            && string.Equals(segments[0] + "/" + segments[1], repositorySlug, StringComparison.OrdinalIgnoreCase)
            && segments[2] == "pull"
            && int.TryParse(segments[3], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number)
            && number > 0;
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
    }
}
