using System;
using System.Collections.Generic;

public sealed class MainThreadPacketQueue
{
    public const int MaxQueuedBytes = 1024 * 1024;
    public const int MaxQueuedPackets = 16384;
    private readonly object _lock = new();
    private readonly Queue<(Action Action, int Bytes)> _queue = new();
    private int _bytes;

    public int Count { get { lock (_lock) return _queue.Count; } }
    public int QueuedBytes { get { lock (_lock) return _bytes; } }

    public bool TryEnqueue(Action action, int bytes)
    {
        lock (_lock)
        {
            if (bytes < 0 || bytes > MaxQueuedBytes - _bytes || _queue.Count >= MaxQueuedPackets)
                return false;
            _queue.Enqueue((action, bytes));
            _bytes += bytes;
            return true;
        }
    }

    public bool TryDequeue(out Action action)
    {
        lock (_lock)
        {
            if (_queue.Count == 0)
            {
                action = null;
                return false;
            }
            var packet = _queue.Dequeue();
            _bytes -= packet.Bytes;
            action = packet.Action;
            return true;
        }
    }
}
