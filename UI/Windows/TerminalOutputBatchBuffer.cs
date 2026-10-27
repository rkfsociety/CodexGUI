using System;
using System.Collections.Generic;

namespace CodexGUI;

internal sealed record TerminalOutputBatch(IReadOnlyList<CodexTerminalOutput> Items, IReadOnlyList<string> TruncatedProcessIds);

internal sealed class TerminalOutputBatchBuffer
{
    internal const int DefaultMaximumCharacters = 256 * 1024;
    private const int DefaultMaximumChunks = 2048;
    private readonly object _sync = new();
    private readonly Queue<CodexTerminalOutput> _pending = new();
    private readonly int _maximumCharacters;
    private readonly int _maximumChunks;
    private int _pendingCharacters;
    private readonly HashSet<string> _truncatedProcessIds = new(StringComparer.Ordinal);
    private bool _flushScheduled;

    internal TerminalOutputBatchBuffer(
        int maximumCharacters = DefaultMaximumCharacters,
        int maximumChunks = DefaultMaximumChunks)
    {
        if (maximumCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        if (maximumChunks <= 0) throw new ArgumentOutOfRangeException(nameof(maximumChunks));
        _maximumCharacters = maximumCharacters;
        _maximumChunks = maximumChunks;
    }

    internal bool Enqueue(CodexTerminalOutput output, bool sourceTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Text.Length > _maximumCharacters)
        {
            var start = output.Text.Length - _maximumCharacters;
            // Do not turn a valid UTF-16 surrogate pair into a dangling low surrogate.
            if (char.IsLowSurrogate(output.Text[start]) && char.IsHighSurrogate(output.Text[start - 1]))
                start++;
            output = output with { Text = output.Text[start..] };
            sourceTruncated = true;
        }

        lock (_sync)
        {
            if (sourceTruncated) _truncatedProcessIds.Add(output.ProcessId);
            if (output.Text.Length > 0)
            {
                while (_pending.Count > 0 &&
                       (_pendingCharacters + output.Text.Length > _maximumCharacters || _pending.Count >= _maximumChunks))
                {
                    var dropped = _pending.Dequeue();
                    _pendingCharacters -= dropped.Text.Length;
                    _truncatedProcessIds.Add(dropped.ProcessId);
                }

                _pending.Enqueue(output);
                _pendingCharacters += output.Text.Length;
            }

            if (_flushScheduled || (_pending.Count == 0 && _truncatedProcessIds.Count == 0)) return false;
            _flushScheduled = true;
            return true;
        }
    }

    internal TerminalOutputBatch Drain()
    {
        lock (_sync)
        {
            var items = _pending.ToArray();
            var truncatedProcessIds = new List<string>(_truncatedProcessIds);
            _pending.Clear();
            _pendingCharacters = 0;
            _truncatedProcessIds.Clear();
            _flushScheduled = false;
            return new TerminalOutputBatch(items, truncatedProcessIds);
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _pending.Clear();
            _pendingCharacters = 0;
            _truncatedProcessIds.Clear();
            _flushScheduled = false;
        }
    }
}
