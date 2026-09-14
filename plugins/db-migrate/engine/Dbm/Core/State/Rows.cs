namespace Dbm.Core.State;

public enum Side { Src, Tgt }
public enum PhaseName { Setup, Discovery, Analysis, Mapping, Sql, Ready, Transfer, Complete }
public enum PhaseStatus { Pending, Running, Drafting, AwaitingReview, Reworking, Approved, Stale }
public enum FeedbackStatus { Draft, Open, Addressed, Declined, Superseded }
public enum JobStatus { Queued, Running, Done, Failed }

public sealed record ProjectRow(string Name, DateTimeOffset CreatedAt, bool Paused, DateTimeOffset? AgentSeenAt);

public sealed record ProjectSettings
{
    public bool SampleValues { get; init; } = true;
    public int ProfileSampleRows { get; init; } = 100_000;
    public double AutoAcceptScore { get; init; } = 0.85;
    public double CandidateScore { get; init; } = 0.50;
}

public sealed record PhaseRow(PhaseName Name, int Ordinal, PhaseStatus Status, int? CurrentVersion, int? ApprovedVersion,
    string? ApprovedFingerprint, DateTimeOffset UpdatedAt);

public sealed record ArtifactRow(long Id, PhaseName Phase, int Version, string PayloadJson, string Author, string? Summary,
    DateTimeOffset CreatedAt);

public sealed record ArtifactMeta(int Version, string Author, string? Summary, DateTimeOffset CreatedAt);

public sealed record FeedbackRow(long Id, PhaseName Phase, int Version, string? Anchor, string Text, FeedbackStatus Status,
    string? Response, int? RespondedVersion, DateTimeOffset CreatedAt);

public sealed record EventRow(long Id, DateTimeOffset Ts, string Type, string PayloadJson);

public sealed record JobRow(long Id, string Kind, PhaseName? Phase, JobStatus Status, string? Error, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);
