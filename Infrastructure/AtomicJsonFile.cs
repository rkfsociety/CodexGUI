using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexGUI;

internal static class AtomicJsonFile
{
    public static void Write(string path, object value, JsonSerializerOptions? options = null)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, options));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { TryDeleteTemporaryFile(temporaryPath); }
    }

    public static async Task WriteAsync(string path, object value, JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(value, options), cancellationToken);
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
