using Dbm.Core.State;

namespace Dbm.Core.Transfer;

/// <summary>Rows/second over a sliding time window (always keeps the last two samples).</summary>
public sealed class RateWindow(TimeSpan window)
{
    private readonly Queue<(DateTimeOffset T, long V)> _samples = new();
    private (DateTimeOffset T, long V) _last;

    public void Add(DateTimeOffset t, long value)
    {
        _samples.Enqueue((t, value));
        _last = (t, value);
        while (_samples.Count > 2 && t - _samples.Peek().T > window) _samples.Dequeue();
    }

    public double PerSecond
    {
        get
        {
            if (_samples.Count < 2) return 0;
            var first = _samples.Peek();
            double seconds = (_last.T - first.T).TotalSeconds;
            return seconds <= 0 ? 0 : Math.Max(0, (_last.V - first.V) / seconds);
        }
    }
}

public sealed record ProgressTask(string TaskId, string Target, string Status, long RowsDone, long? RowsSource, long RowsError, double RowsPerSec);

/// <summary>
/// <see cref="Total"/> is the sum of the task source counts that are known. <see cref="TasksWithoutSource"/> says how many tasks have
/// not been counted yet, so a total that is short says why it is short; while it is non-zero <see cref="EtaSec"/> is null, because an
/// estimate priced from an understated total reads as "nearly done" while a whole table is still unread.
/// </summary>
public sealed record ProgressOverall(long Done, long Total, long RowsError, double RowsPerSec, double? EtaSec)
{
    public int TasksWithoutSource { get; init; }
}

public sealed record ProgressSnapshot(long RunId, string Status, List<ProgressTask> Tasks, ProgressOverall Overall);

/// <summary>Live counters for the UI; publishes non-persisted "transfer_progress" events at most every 250 ms.</summary>
public sealed class TransferProgress
{
    public static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private sealed class TaskState
    {
        public required string TaskId { get; init; }
        public required string Target { get; init; }
        public TransferTaskStatus Status { get; set; }
        public long Committed { get; set; }
        public long InFlight { get; set; }
        public long Errors { get; set; }
        public long? Source { get; set; }
        public RateWindow Rate { get; } = new(Window);
        public long Done => Committed + InFlight;
    }

    private readonly object _lock = new();
    private readonly List<TaskState> _tasks;
    private readonly RateWindow _overall = new(Window);
    private readonly long _runId;
    private readonly IEventSink _sink;
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _lastPublish = DateTimeOffset.MinValue;

    public TransferProgress(long runId, IEventSink sink, IEnumerable<TransferTaskRow> tasks, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(tasks);
        _runId = runId;
        _sink = sink;
        _now = now ?? (() => Clock.Now());
        _tasks = tasks.OrderBy(t => t.Ordinal).Select(t => new TaskState
        {
            TaskId = t.TaskId, Target = t.Target, Status = t.Status, Committed = t.RowsDone, Errors = t.RowsError, Source = t.RowsSource,
        }).ToList();
    }

    public string RunStatus { get; set; } = "running";

    public void SetStatus(string taskId, TransferTaskStatus status)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        lock (_lock)
        {
            var t = Find(taskId);
            if (t is null) return;
            t.Status = status;
            if (status != TransferTaskStatus.Running) t.InFlight = 0;
        }
        Publish(force: true);
    }

    public void SetSource(string taskId, long? rowsSource)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        lock (_lock)
        {
            var t = Find(taskId);
            if (t is not null) t.Source = rowsSource;
        }
    }

    public void Committed(string taskId, long rowsDone, long rowsError)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        lock (_lock)
        {
            var t = Find(taskId);
            if (t is null) return;
            t.Committed = rowsDone;
            t.InFlight = 0;
            t.Errors = rowsError;
            Sample(t);
        }
        Publish(force: false);
    }

    public void InFlight(string taskId, long rowsInFlight)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        lock (_lock)
        {
            var t = Find(taskId);
            if (t is null) return;
            t.InFlight = rowsInFlight;
            Sample(t);
        }
        Publish(force: false);
    }

    public ProgressSnapshot Snapshot()
    {
        lock (_lock)
        {
            var tasks = _tasks.Select(t => new ProgressTask(t.TaskId, t.Target, EnumText.ToText(t.Status), t.Done, t.Source, t.Errors,
                Math.Round(t.Status == TransferTaskStatus.Running ? t.Rate.PerSecond : 0, 1))).ToList();
            long total = _tasks.Sum(t => t.Source ?? 0);
            int unknown = _tasks.Count(t => t.Source is null);
            long done = _tasks.Sum(t => t.Done);
            long errors = _tasks.Sum(t => t.Errors);
            double rate = Math.Round(_overall.PerSecond, 1);
            long remaining = Math.Max(0, total - done - errors);
            double? eta = rate > 0 && unknown == 0 ? Math.Round(remaining / rate, 0) : null;
            return new ProgressSnapshot(_runId, RunStatus, tasks,
                new ProgressOverall(done, total, errors, rate, eta) { TasksWithoutSource = unknown });
        }
    }

    public bool Publish(bool force)
    {
        ProgressSnapshot snapshot;
        lock (_lock)
        {
            var now = _now();
            if (!force && now - _lastPublish < Throttle) return false;
            _lastPublish = now;
            snapshot = Snapshot();
        }
        _sink.Publish("transfer_progress", snapshot, persist: false);
        return true;
    }

    private TaskState? Find(string taskId) => _tasks.FirstOrDefault(t => t.TaskId == taskId);

    private void Sample(TaskState t)
    {
        var now = _now();
        t.Rate.Add(now, t.Done);
        _overall.Add(now, _tasks.Sum(x => x.Done));
    }
}
