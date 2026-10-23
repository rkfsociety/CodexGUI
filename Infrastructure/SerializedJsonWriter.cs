using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexGUI;

// Snapshots are detached from UI-owned mutable collections before they enter this queue.
internal sealed class SerializedJsonWriter
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public Task Enqueue(string path, JsonElement snapshot, JsonSerializerOptions? options = null,
        string? backupPath = null)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(previous =>
            {
                // Each caller observes its own result; a failed write must not poison later retries.
                _ = previous.Exception;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                AtomicJsonFile.Write(path, snapshot, options, backupPath);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return _tail;
        }
    }
}
