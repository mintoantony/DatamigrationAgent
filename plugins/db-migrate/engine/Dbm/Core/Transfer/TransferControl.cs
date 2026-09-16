namespace Dbm.Core.Transfer;

public enum StopKind { None, Pause, Cancel, Fail }

/// <summary>One committed chunk. <see cref="MergeRowsAffected"/> is null for a task with no <c>MergeSql</c>, for a merge that never
/// ran, and for one that ran and reported no count at all (ruling 74) - the runner surfaces the number, it does not judge it. Which of
/// the three a null means is <see cref="MergeStatus"/>'s answer, never the count's (ruling 89).</summary>
public sealed record ChunkCommit(string TaskId, string Target, int ChunkNo, long RowsDone, long RowsError)
{
    /// <summary>Required for the same reason the loader's own is: the default, <see cref="Transfer.MergeStatus.NotApplicable"/>, is the
    /// value that says no merge exists, which is the one answer a subscriber must never be handed by accident.</summary>
    public required MergeStatus MergeStatus { get; init; }

    public long? MergeRowsAffected { get; init; }
}

/// <summary>Cooperative stop signal for one run segment; workers check it between chunks ("finish the current chunk").</summary>
public sealed class TransferControl
{
    private int _kind;

    public StopKind Kind => (StopKind)Volatile.Read(ref _kind);
    public bool StopRequested => Kind != StopKind.None;

    /// <summary>Raised synchronously on the worker right after a chunk (with its checkpoint) committed.</summary>
    public event Action<ChunkCommit>? ChunkCommitted;

    public void RequestPause() => Interlocked.CompareExchange(ref _kind, (int)StopKind.Pause, (int)StopKind.None);

    public void RequestCancel()
    {
        while (true)
        {
            int current = Volatile.Read(ref _kind);
            if (current == (int)StopKind.Fail || current == (int)StopKind.Cancel) return;
            if (Interlocked.CompareExchange(ref _kind, (int)StopKind.Cancel, current) == current) return;
        }
    }

    public void RequestFail() => Interlocked.Exchange(ref _kind, (int)StopKind.Fail);

    internal void OnChunkCommitted(ChunkCommit commit) => ChunkCommitted?.Invoke(commit);
}
