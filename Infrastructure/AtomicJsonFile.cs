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
                JsonSerializer.Serialize(new BoundedWriteStream(stream, MaximumJsonBytes), value, options);
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
            // Enforce the limit during parsing as well: a file may grow after Length was checked.
            using var boundedStream = new BoundedReadStream(stream, MaximumJsonBytes);
            var value = JsonSerializer.Deserialize<T>(boundedStream)
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
                await JsonSerializer.SerializeAsync(new BoundedWriteStream(stream, MaximumJsonBytes), value, options, cancellationToken);
                if (stream.Length > MaximumJsonBytes)
                    throw new InvalidDataException("Файл JSON превышает допустимый размер 128 МиБ.");
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { TryDeleteTemporaryFile(temporaryPath); }
    }


    // Refuse oversized snapshots while they are being serialized, not after a
    // temporary file has already consumed more disk space than the limit.
    private sealed class BoundedWriteStream(Stream destination, long maximumBytes) : Stream
    {
        private long _bytesWritten;

        private void Reserve(int count)
        {
            if (count > maximumBytes - _bytesWritten)
                throw new InvalidDataException("Файл JSON превышает допустимый размер 128 МиБ.");
            _bytesWritten += count;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Reserve(count);
            destination.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            destination.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Reserve(count);
            return destination.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reserve(buffer.Length);
            return destination.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // JsonSerializer reads streams incrementally. Check the byte count on every read
    // instead of trusting the file size observed before deserialization.
    private sealed class BoundedReadStream(Stream source, long maximumBytes) : Stream
    {
        private long _bytesRead;

        private int RecordRead(int count)
        {
            _bytesRead += count;
            if (_bytesRead > maximumBytes)
                throw new InvalidDataException("Файл JSON превышает допустимый размер 128 МиБ.");
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => RecordRead(source.Read(buffer, offset, (int)Math.Min(count, maximumBytes - _bytesRead + 1)));

        public override int Read(Span<byte> buffer)
            => RecordRead(source.Read(buffer[..(int)Math.Min(buffer.Length, maximumBytes - _bytesRead + 1)]));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
