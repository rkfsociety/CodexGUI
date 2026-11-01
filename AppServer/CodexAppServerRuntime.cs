using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Formats.Tar;

namespace CodexGUI;

internal sealed record AppServerRuntimeProgress(string Status, long BytesReceived = 0, long? TotalBytes = null);

internal static class CodexAppServerRuntime
{
    public const string Version = "0.163.0";
    private const string PackageSha256 = "abee5d71e80c4707b26f4c3c7b65b0f736ed3b7e7918b4f9b14a94ad89ff781c";
    private const string PackageName = "codex-app-server-package-x86_64-pc-windows-msvc.tar.gz";
    private const string ReleaseBaseUrl = "https://releases.openai.com/codex/releases/";
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    public static bool UseInstalledCliFallback { get; set; }

    public static string RuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "app-server-runtimes");

    public static string RuntimeDirectory => Path.Combine(RuntimeRoot, $"{Version}-{PackageSha256[..8]}");
    public static string RuntimeExecutablePath => Path.Combine(RuntimeDirectory, "bin", "codex-app-server.exe");

    // A complete-looking cache must not be trusted when any of its files or parent
    // folders are redirected through a junction or symbolic link.
    public static bool IsInstalled => RequiredPackageFiles.All(relativePath =>
    {
        var file = Path.Combine(RuntimeDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return !HasReparsePoint(file) && File.Exists(file);
    });

    private static readonly string[] RequiredPackageFiles =
    [
        "codex-package.json",
        "bin/codex-app-server.exe",
        "bin/codex-code-mode-host.exe",
        "codex-path/rg.exe",
        "codex-resources/codex-command-runner.exe",
        "codex-resources/codex-windows-sandbox-setup.exe"
    ];

    public static string ResolveExecutable()
    {
        if (IsInstalled) return RuntimeExecutablePath;

        if (UseInstalledCliFallback || AppInstaller.IsDevelopmentCheckout(Environment.ProcessPath ?? ""))
        {
            try { return CodexCliService.ResolveExecutable(); }
            catch (FileNotFoundException) { }
        }

        throw new FileNotFoundException(
            $"Не установлен Codex App Server {Version}. Запустите CodexGUI при подключении к интернету.",
            RuntimeExecutablePath);
    }

    public static bool TryResolveInstalledCli(out string? executablePath)
    {
        try
        {
            executablePath = CodexCliService.ResolveExecutable();
            return true;
        }
        catch (FileNotFoundException)
        {
            executablePath = null;
            return false;
        }
    }

    public static void AddProcessArguments(ProcessStartInfo startInfo, string executablePath)
    {
        if (string.Equals(Path.GetFileName(executablePath), "codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");
            return;
        }

        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");
    }

    public static async Task EnsureInstalledAsync(IProgress<AppServerRuntimeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsInstalled) return;
        // Reject linked cache roots before downloading a large package or writing files.
        if (HasReparsePoint(RuntimeRoot) || HasReparsePoint(RuntimeDirectory))
            throw new IOException("Путь Codex App Server содержит junction или symbolic link.");
        var archivePath = Path.Combine(Path.GetTempPath(), $"CodexGUI-app-server-{Guid.NewGuid():N}.tar.gz");
        var stagingDirectory = RuntimeDirectory + ".stage-" + Guid.NewGuid().ToString("N");
        try
        {
            progress?.Report(new AppServerRuntimeProgress($"Скачиваю Codex App Server {Version}…"));
            await DownloadAndVerifyPackageAsync(archivePath, progress, cancellationToken);

            Directory.CreateDirectory(RuntimeRoot);
            if (HasReparsePoint(RuntimeRoot))
                throw new IOException("Папка runtime CodexGUI не должна содержать junction или symbolic link.");
            Directory.CreateDirectory(stagingDirectory);
            if (HasReparsePoint(stagingDirectory))
                throw new IOException("Временная папка Codex App Server содержит junction или symbolic link.");
            progress?.Report(new AppServerRuntimeProgress("Распаковываю проверенный пакет…"));
            await Task.Run(() =>
            {
                using var archive = File.OpenRead(archivePath);
                using var gzip = new GZipStream(archive, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, stagingDirectory, overwriteFiles: false);
            }, cancellationToken);

            foreach (var relativePath in RequiredPackageFiles)
            {
                var requiredPath = Path.Combine(stagingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (HasReparsePoint(requiredPath) || !File.Exists(requiredPath))
                    throw new InvalidDataException($"В официальном пакете отсутствует безопасный файл {relativePath}.");
            }

            // A cache directory may have been replaced while the archive was unpacked.
            if (HasReparsePoint(RuntimeRoot) || HasReparsePoint(RuntimeDirectory)
                || HasReparsePoint(stagingDirectory))
                throw new IOException("Путь Codex App Server был перенаправлен через файловую ссылку.");

            // Preserve an incomplete installation until a verified replacement is ready.
            if (Directory.Exists(RuntimeDirectory) && !IsInstalled)
            {
                Directory.Move(RuntimeDirectory, RuntimeDirectory + ".invalid-" + Guid.NewGuid().ToString("N"));
            }

            try { Directory.Move(stagingDirectory, RuntimeDirectory); }
            catch (IOException) when (IsInstalled)
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
            progress?.Report(new AppServerRuntimeProgress($"Codex App Server {Version} готов."));
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                try { Directory.Delete(stagingDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            try { File.Delete(archivePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task DownloadAndVerifyPackageAsync(string archivePath,
        IProgress<AppServerRuntimeProgress>? progress, CancellationToken cancellationToken)
    {
        var url = new Uri($"{ReleaseBaseUrl}{Version}/{PackageName}");
        using var client = CreateClient();
        using var response = await AppUpdater.GetResponseAsync(client, url,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
        if (totalBytes is > MaximumPackageBytes)
            throw new InvalidDataException("Размер пакета Codex App Server превышает допустимый предел.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[81920];
        long bytesReceived = 0;
        while (true)
        {
            idleCancellation.CancelAfter(TimeSpan.FromSeconds(30));
            int count;
            try { count = await source.ReadAsync(buffer.AsMemory(), idleCancellation.Token); }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Загрузка Codex App Server остановилась более чем на 30 секунд.", error);
            }
            finally { idleCancellation.CancelAfter(Timeout.InfiniteTimeSpan); }
            if (count == 0) break;
            if (count > MaximumPackageBytes - bytesReceived)
                throw new InvalidDataException("Загрузка пакета Codex App Server превысила допустимый предел.");

            hash.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            bytesReceived += count;
            progress?.Report(new AppServerRuntimeProgress("Скачиваю Codex App Server…", bytesReceived, totalBytes));
        }

        await destination.FlushAsync(cancellationToken);
        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!actualHash.Equals(PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 пакета Codex App Server не совпал: ожидается {PackageSha256}, получен {actualHash}.");
    }

    private static bool HasReparsePoint(string path)
    {
        // Include the user's profile and other existing ancestors, not only the
        // final cache directory. Missing components are expected during installation.
        for (string? current = Path.GetFullPath(path); current is not null;
             current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { return true; }
        }
        return false;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CodexGUI", AppUpdater.CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return client;
    }
}
