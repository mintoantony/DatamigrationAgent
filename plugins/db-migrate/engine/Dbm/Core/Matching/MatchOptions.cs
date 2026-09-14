namespace Dbm.Core.Matching;

/// <summary>Auto-mapper bands (spec §4.4): score ≥ AutoAccept is auto-accepted, ≥ Candidate is proposed, below is unmapped.</summary>
public sealed record MatchOptions(double AutoAccept = 0.85, double Candidate = 0.50, int TopK = 3);
