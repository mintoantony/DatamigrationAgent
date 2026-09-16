using System.Text.Json.Serialization;

namespace Dbm.Core.Transfer;

public sealed record TransferOptions
{
    public int ChunkSize { get; init; } = 100_000;
    public int Parallelism { get; init; } = 4;
    public string ErrorMode { get; init; } = "stop";     // "stop" | "skip"
    public bool TruncateTarget { get; init; }
    public bool TableLock { get; init; }
    public bool ValidateChecksums { get; init; } = true;
    public bool FireTriggers { get; init; }
    public bool KeepControlTable { get; init; }

    [JsonIgnore] public bool SkipErrors => ErrorMode == "skip";

    /// <summary>Clamps sizes and canonicalises ErrorMode; unknown ErrorMode -> TransferException("bad_options").</summary>
    public TransferOptions Normalized()
    {
        string mode = (ErrorMode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("stop" or "skip"))
            throw new TransferException("bad_options", $"errorMode must be \"stop\" or \"skip\" (got \"{ErrorMode}\").");
        return this with
        {
            ChunkSize = Math.Clamp(ChunkSize, 1, 10_000_000),
            Parallelism = Math.Clamp(Parallelism, 1, 32),
            ErrorMode = mode,
        };
    }
}

public enum RunStatus { Pending, Running, Paused, Completed, Failed, Cancelled }

public enum TransferTaskStatus { Pending, Running, Paused, Done, Failed }
