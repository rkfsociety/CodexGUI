using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

internal static class AppUpdater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/rkfsociety/CodexGUI/releases/latest";
    private const string ArchiveName = "CodexGUI-win-x64.zip";
    private const string ChecksumName = ArchiveName + ".sha256";
    private const string SignatureName = ArchiveName + ".sig";
    private const string ReleasePublicKey = "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjKDFJz59PcWpOLTJqcxFN3FsM+Ah\nvbzdlyTJhKnLZ93rxwqsrOGJOigTcx6Ntn1DlNg5vVA6AihvsTZOXq+rDQ==\n-----END PUBLIC KEY-----";
    private static readonly HttpClient Client = CreateClient();

    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.001";

    public static async Task<AppUpdate?> FindUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await GetResponseAsync(Client, new Uri(LatestReleaseApi), HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (tag is null || !tag.StartsWith('v')) return null;
        var latestVersion = tag[1..];
        if (!IsVersionFormat(latestVersion) || !IsVersionFormat(CurrentVersion)) return null;
        if (CompareVersions(latestVersion, CurrentVersion) <= 0) return null;

        var assets = root.GetProperty("assets");
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

    public static async Task<PreparedAppUpdate> DownloadAndVerifyAsync(AppUpdate update, CancellationToken cancellationToken = default)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "CodexGUI-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var archivePath = Path.Combine(tempDirectory, ArchiveName);
        var checksumPath = Path.Combine(tempDirectory, ChecksumName);
        var signaturePath = Path.Combine(tempDirectory, SignatureName);

        try
        {
            await DownloadFileAsync(update.ArchiveUri, archivePath, cancellationToken);
            await DownloadFileAsync(update.ChecksumUri, checksumPath, cancellationToken);
            await DownloadFileAsync(update.SignatureUri, signaturePath, cancellationToken);
            var expectedHash = (await File.ReadAllTextAsync(checksumPath, cancellationToken)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (expectedHash is null || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Файл контрольной суммы релиза повреждён.");

            await using var archive = File.OpenRead(archivePath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archive, cancellationToken));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма архива не совпала.");

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
        var asset = assets.EnumerateArray().FirstOrDefault(item => item.GetProperty("name").GetString() == name);
        return asset.ValueKind == JsonValueKind.Undefined ? null : asset.GetProperty("browser_download_url").GetString();
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

    private static async Task DownloadFileAsync(Uri uri, string path, CancellationToken cancellationToken)
    {
        using var response = await GetResponseAsync(Client, uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(path);
        await CopyDownloadAsync(source, destination, TimeSpan.FromSeconds(30), cancellationToken);
    }

    internal static async Task<HttpResponseMessage> GetResponseAsync(HttpClient client, Uri uri,
        HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await client.GetAsync(uri, completionOption, cancellationToken);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                // GET is safe to repeat once after a transient timeout.
                if (attempt == 0) continue;
                throw new TimeoutException($"Сервер {uri.Host} не ответил вовремя после двух попыток. "
                    + "Проверьте подключение к GitHub и повторите проверку обновлений позже.", error);
            }
        }
    }

    internal static async Task CopyDownloadAsync(Stream source, Stream destination, TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
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
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
}
