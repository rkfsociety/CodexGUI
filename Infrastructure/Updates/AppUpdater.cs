using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexGUI;

internal sealed record AppUpdate(string Version, Uri ArchiveUri, Uri ChecksumUri, Uri SignatureUri);
internal sealed record PreparedAppUpdate(AppUpdate Update, string TempDirectory, string ArchivePath);
internal sealed record UpdateDownloadProgress(string Stage, int? Percent, long DownloadedBytes = 0, long? TotalBytes = null);

internal static class AppUpdater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/rkfsociety/CodexGUI/releases/latest";
    private const string ArchiveName = "CodexGUI-win-x64.zip";
    private const string ChecksumName = ArchiveName + ".sha256";
    private const string SignatureName = ArchiveName + ".sig";
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private const long MaximumMetadataBytes = 4096;
    private const long MaximumReleaseMetadataBytes = 2L * 1024 * 1024;
    private const string ReleasePublicKey = "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjKDFJz59PcWpOLTJqcxFN3FsM+Ah\nvbzdlyTJhKnLZ93rxwqsrOGJOigTcx6Ntn1DlNg5vVA6AihvsTZOXq+rDQ==\n-----END PUBLIC KEY-----";
    private static readonly HttpClient Client = CreateClient();

    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("Не удалось определить версию CodexGUI.");

    public static async Task<AppUpdate?> FindUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await GetResponseAsync(Client, new Uri(LatestReleaseApi), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumReleaseMetadataBytes)
            throw new InvalidDataException("Метаданные релиза GitHub превышают допустимый размер.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var metadata = new MemoryStream();
        await CopyDownloadAsync(stream, metadata, TimeSpan.FromSeconds(30), cancellationToken, MaximumReleaseMetadataBytes);
        metadata.Position = 0;
        using var document = await JsonDocument.ParseAsync(metadata, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("GitHub вернул некорректные метаданные релиза.");
        var tag = tagElement.GetString();
        if (tag is null || !tag.StartsWith('v')) return null;
        var latestVersion = tag[1..];
        if (!IsVersionFormat(latestVersion) || !IsVersionFormat(CurrentVersion)) return null;
        if (CompareVersions(latestVersion, CurrentVersion) <= 0) return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("В метаданных релиза GitHub отсутствует список файлов.");
        var archiveUrl = FindAssetUrl(assets, ArchiveName);
        var checksumUrl = FindAssetUrl(assets, ChecksumName);
        var signatureUrl = FindAssetUrl(assets, SignatureName);
        if (!Uri.TryCreate(archiveUrl, UriKind.Absolute, out var archiveUri)
            || archiveUri.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(checksumUrl, UriKind.Absolute, out var checksumUri)
            || checksumUri.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(signatureUrl, UriKind.Absolute, out var signatureUri)
            || signatureUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("В релизе отсутствует архив, SHA-256 или подпись.");

        return new AppUpdate(latestVersion, archiveUri, checksumUri, signatureUri);
    }

    public static async Task<PreparedAppUpdate> DownloadAndVerifyAsync(AppUpdate update, CancellationToken cancellationToken = default,
        IProgress<UpdateDownloadProgress>? progress = null)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "CodexGUI-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var archivePath = Path.Combine(tempDirectory, ArchiveName);
        var checksumPath = Path.Combine(tempDirectory, ChecksumName);
        var signaturePath = Path.Combine(tempDirectory, SignatureName);

        try
        {
            progress?.Report(new UpdateDownloadProgress("Загрузка архива", 0));
            var lastReportedPercent = -1;
            long lastReportedBytes = 0;
            await DownloadFileAsync(update.ArchiveUri, archivePath, MaximumArchiveBytes, cancellationToken,
                (received, total) =>
                {
                    var percent = total is > 0
                        ? (int?)Math.Clamp((int)(received * 90.0 / total.Value), 0, 90)
                        : null;
                    // Avoid posting a WPF UI callback for every network chunk.
                    if (total is > 0 && percent == lastReportedPercent)
                        return;
                    if (total is not > 0 && received - lastReportedBytes < 1024 * 1024)
                        return;
                    lastReportedPercent = percent ?? -1;
                    lastReportedBytes = received;
                    progress?.Report(new UpdateDownloadProgress("Загрузка архива", percent, received, total));
                });
            progress?.Report(new UpdateDownloadProgress("Загрузка метаданных и подписи", 92));
            await DownloadFileAsync(update.ChecksumUri, checksumPath, MaximumMetadataBytes, cancellationToken);
            await DownloadFileAsync(update.SignatureUri, signaturePath, MaximumMetadataBytes, cancellationToken);
            progress?.Report(new UpdateDownloadProgress("Проверка SHA-256", 96));
            var expectedHash = (await File.ReadAllTextAsync(checksumPath, cancellationToken)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (expectedHash is null || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Файл контрольной суммы релиза повреждён.");

            await using var archive = File.OpenRead(archivePath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archive, cancellationToken));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма архива не совпала.");

            progress?.Report(new UpdateDownloadProgress("Проверка подписи релиза", 99));
            byte[] signature;
            try { signature = Convert.FromBase64String((await File.ReadAllTextAsync(signaturePath, cancellationToken)).Trim()); }
            catch (FormatException error) { throw new InvalidDataException("Подпись архива имеет некорректный формат.", error); }
            var signedManifest = Encoding.UTF8.GetBytes($"{update.Version}\n{actualHash.ToLowerInvariant()}  {ArchiveName}");
            using (var verifier = ECDsa.Create())
            {
                verifier.ImportFromPem(ReleasePublicKey);
                if (!verifier.VerifyData(signedManifest, signature, HashAlgorithmName.SHA256))
                    throw new InvalidDataException("Подпись манифеста обновления неверна. Обновление отменено.");
            }

            return new PreparedAppUpdate(update, tempDirectory, archivePath);
        }
        catch
        {
            try { Directory.Delete(tempDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static async Task InstallPreparedUpdateAsync(PreparedAppUpdate preparedUpdate)
    {
        var updaterPath = Path.Combine(preparedUpdate.TempDirectory, "Update-CodexGUI.ps1");
        using var updaterResource = typeof(AppUpdater).Assembly.GetManifestResourceStream("CodexGUI.Update-CodexGUI.ps1")
            ?? throw new FileNotFoundException("Не найден встроенный сценарий установки обновления.");
        await using (var updaterFile = File.Create(updaterPath))
            await updaterResource.CopyToAsync(updaterFile);

        var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь приложения.");
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(updaterPath);
        startInfo.ArgumentList.Add("-ArchivePath");
        startInfo.ArgumentList.Add(preparedUpdate.ArchivePath);
        startInfo.ArgumentList.Add("-TargetDirectory");
        startInfo.ArgumentList.Add(AppContext.BaseDirectory);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-ExecutableName");
        startInfo.ArgumentList.Add(Path.GetFileName(executablePath));

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить установщик обновления.");
    }

    public static void DiscardPreparedUpdate(PreparedAppUpdate preparedUpdate)
    {
        try { Directory.Delete(preparedUpdate.TempDirectory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CodexGUI", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string? FindAssetUrl(JsonElement assets, string name)
    {
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("name", out var assetName)
                || assetName.ValueKind != JsonValueKind.String
                || assetName.GetString() != name)
                continue;
            return asset.TryGetProperty("browser_download_url", out var url) && url.ValueKind == JsonValueKind.String
                ? url.GetString()
                : null;
        }

        return null;
    }

    internal static bool IsVersionFormat(string version) =>
        version.Split('.') is { Length: 3 } parts
        && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));

    internal static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < 3; index++)
        {
            var leftNumber = leftParts[index].TrimStart('0');
            var rightNumber = rightParts[index].TrimStart('0');
            if (leftNumber.Length != rightNumber.Length)
                return leftNumber.Length.CompareTo(rightNumber.Length);

            var comparison = string.CompareOrdinal(leftNumber, rightNumber);
            if (comparison != 0) return comparison;
        }

        return 0;
    }

    private static async Task DownloadFileAsync(Uri uri, string path, long maximumBytes, CancellationToken cancellationToken,
        Action<long, long?>? onProgress = null)
    {
        using var response = await GetResponseAsync(Client, uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        if (total is { } contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("Размер файла обновления превышает допустимый предел.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(path);
        await CopyDownloadAsync(source, destination, TimeSpan.FromSeconds(30), cancellationToken, maximumBytes,
            count => onProgress?.Invoke(count, total));
    }

    internal static async Task<HttpResponseMessage> GetResponseAsync(HttpClient client, Uri uri,
        HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await client.GetAsync(uri, completionOption, cancellationToken);
                if (attempt < 2 && response.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta;
                    response.Dispose();
                    var delay = retryAfter is { } requested && requested > TimeSpan.Zero
                        ? TimeSpan.FromSeconds(Math.Min(requested.TotalSeconds, 10))
                        : TimeSpan.FromMilliseconds(500 * (attempt + 1));
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                // GET is idempotent: retry once if request headers time out.
                if (attempt == 0) continue;
                throw new TimeoutException($"Сервер {uri.Host} не ответил вовремя после повторной попытки. "
                    + "Проверьте подключение к интернету и повторите операцию позже.", error);
            }
        }
    }

    internal static async Task CopyDownloadAsync(Stream source, Stream destination, TimeSpan idleTimeout,
        CancellationToken cancellationToken, long maximumBytes = long.MaxValue,
        Action<long>? onProgress = null)
    {
        var buffer = new byte[81920];
        long transferred = 0;
        using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            idleCancellation.CancelAfter(idleTimeout);
            int count;
            try
            {
                count = await source.ReadAsync(buffer.AsMemory(), idleCancellation.Token);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Скачивание обновления остановилось: сервер перестал передавать данные. "
                    + "Проверьте подключение к GitHub и повторите проверку обновлений позже.", error);
            }
            finally
            {
                idleCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            }

            if (count == 0) return;
            if (count > maximumBytes - transferred)
                throw new InvalidDataException("Размер файла обновления превышает допустимый предел.");
            transferred += count;
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            onProgress?.Invoke(transferred);
        }
    }
}
