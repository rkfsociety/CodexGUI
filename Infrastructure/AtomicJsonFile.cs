using System;
using System.IO;
using System.Text.Json;

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
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string BackupUnreadable(string path)
    {
        var backupPath = path + ".unreadable-" + Guid.NewGuid().ToString("N") + ".bak";
        File.Copy(path, backupPath);
        return backupPath;
    }
}
