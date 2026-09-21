using System.Collections.Concurrent;
using EssentiaDLP.Models;

namespace EssentiaDLP.Services;

public sealed class JobLog
{
    private const int MaxLines = 500;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly object _gate = new();
    private int _count;

    public bool Running { get; private set; }
    public string? Mode { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public object? LastResult { get; private set; }
    public string? Error { get; private set; }

    public void Begin(string mode)
    {
        lock (_gate)
        {
            Running = true;
            Mode = mode;
            StartedAt = DateTimeOffset.UtcNow;
            Error = null;
            LastResult = null;
            while (_lines.TryDequeue(out _)) { }
            _count = 0;
        }
    }

    public void Line(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        _lines.Enqueue(text);
        if (Interlocked.Increment(ref _count) > MaxLines)
        {
            if (_lines.TryDequeue(out _))
                Interlocked.Decrement(ref _count);
        }
    }

    public void Complete(object? result, string? error)
    {
        lock (_gate)
        {
            Running = false;
            LastResult = result;
            Error = error;
        }
    }

    public JobStatus Snapshot()
    {
        lock (_gate)
        {
            return new JobStatus
            {
                Running = Running,
                Mode = Mode,
                StartedAt = StartedAt,
                Logs = _lines.ToArray(),
                LastResult = LastResult,
                Error = Error,
            };
        }
    }
}
