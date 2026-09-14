using System.Text.Json;
using Dbm.Core.Jobs;
using Dbm.Core.Mapping;
using Dbm.Core.State;

namespace Dbm.Core.Matching;

/// <summary>Job "automap": builds the script draft of the mapping from the stored catalogs, carrying over agent/human decisions
/// from the latest Mapping artifact.</summary>
public sealed class AutomapJob : IJobHandler
{
    public string Kind => "automap";

    public Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var s = ctx.Services;
        var src = s.Catalog.Get(Side.Src) ?? throw new InvalidOperationException("source catalog missing; run discovery first");
        var tgt = s.Catalog.Get(Side.Tgt) ?? throw new InvalidOperationException("target catalog missing; run discovery first");
        var synonyms = Synonyms.ForProject(s.Ws);
        var settings = s.Project.GetSettings();
        var options = new MatchOptions(settings.AutoAcceptScore, settings.CandidateScore);

        MappingPayload? carryOver = null;
        var latest = s.Artifacts.Latest(PhaseName.Mapping);
        if (latest is not null)
        {
            try { carryOver = Json.Deserialize<MappingPayload>(latest.PayloadJson); }
            catch (JsonException e) { ctx.Log($"automap: ignoring unreadable mapping v{latest.Version}: {e.Message}"); }
        }
        ctx.Log($"automap: {tgt.Tables.Count} target tables, {src.Tables.Count} source tables, carry-over {(carryOver is null ? "none" : $"v{latest!.Version}")}");
        ct.ThrowIfCancellationRequested();

        var payload = AutoMapper.Map(src, tgt, synonyms, options, carryOver);
        var mapped = payload.Tables.Values.Count(t => t.Sources.Count > 0);
        var attention = MappingValidator.Attention(payload, options).Count;
        var blockers = MappingValidator.Blockers(payload, src, tgt).Count;
        var summary = $"{mapped} tables mapped, {attention} need attention, {blockers} blockers";
        ctx.Log($"automap: {summary}");
        return Task.FromResult(new JobResult(Json.ToNode(payload), summary));
    }
}
