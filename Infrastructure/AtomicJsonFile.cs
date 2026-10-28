using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexGUI;

internal static class AtomicJsonFile
{
    // State and automation metadata should never require loading an unbounded JSON document.
    private const long MaximumJsonBytes = 128L * 1024 * 1024;
    public static void Write(string path, object value, JsonSerializerOptions? options = null, string? backupPath = null)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, options);
                if (stream.Length > MaximumJsonBytes)
                    throw new InvalidDataException("Файл JSON превышает допустимый размер 128 МиБ.");
                stream.Flush(flushToDisk: true);
            }
            if (backupPath is not null && File.Exists(path))
                File.Replace(temporaryPath, path, backupPath);
            else
                File.Move(temporaryPath, path, overwrite: true);
        }
        finally { TryDeleteTemporaryFile(temporaryPath); }
    }

    public static T ReadWithBackup<T>(string path, Action<T> validate) where T : class
    {
        T Read(string candidate)
        {
            // Allow atomic replacement while a snapshot is being read.
            using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 81920, FileOptions.SequentialScan);
            if (stream.Length > MaximumJsonBytes)
                throw new InvalidDataException("Файл JSON превышает допустимый размер 128 МиБ.");
            var value = JsonSerializer.Deserialize<T>(stream)
                ?? throw new InvalidDataException("Файл состояния пуст.");
            validate(value);
            return value;
        }

        try { return Read(path); }
        // Only recover missing or corrupt state; transient access/I/O failures must not roll back a valid primary.
        catch (Exception error) when (error is FileNotFoundException or JsonException or InvalidDataException)
        {
            // Validate the backup before touching either file. Preserve the unreadable primary for recovery.
            T recovered;
            try { recovered = Read(path + ".bak"); }
            catch (Exception backupError) when (backupError is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new InvalidDataException($"Не удалось прочитать состояние ({error.Message}) и резервную копию ({backupError.Message}).", error);
            }
            if (File.Exists(path)) BackupUnreadable(path);
            Write(path, recovered);
            return recovered;
        }
    }

    public static async Task WriteAsync(string path, object value, JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
                if (stream.Length > MaximumJsonBytes)
                    throw new InvalidDataException("Файл JSON превышает допустимый размер 128 МиБ.");
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { TryDeleteTemporaryFile(temporaryPath); }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try { File.Delete(temporaryPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string BackupUnreadable(string path)
    {
        var backupPath = path + ".unreadable-" + Guid.NewGuid().ToString("N") + ".bak";
        File.Copy(path, backupPath);
        return backupPath;
    }
}
