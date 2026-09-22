using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

/// <summary>
/// Open item 33, Ruling 196: the SampleValues project setting (sample values in the profiles Claude reads) has a switch. Off, no
/// sample value reaches Claude: none is stored in the catalogs (turning it off removes those already there, and a discovery that
/// finishes while it is off is cleaned the same way), so neither a work packet nor `dbm show` can carry one.
/// </summary>
public class SampleValuesSettingTests
{
    private const string Email = "first1.last1@example.com";   // a sample value of dbo.CUST.EMAIL_ADDR in SampleCatalogs

    private static IEnumerable<ColumnProfile> Profiles(DbmServices s) =>
        new[] { Side.Src, Side.Tgt }.Select(side => s.Catalog.Get(side)!)
            .SelectMany(c => c.Tables).SelectMany(t => t.Columns).Select(c => c.Profile).OfType<ColumnProfile>();

    private static List<string> Leaks(DbmServices s) =>
        new[] { Side.Src, Side.Tgt }.Select(side => s.Catalog.Get(side)!).SelectMany(c => c.Tables).SelectMany(t => t.Columns
            .Where(c => c.Profile is { } p && (p.Samples.Count > 0
                                               || (TypeTraits.IsString(c.DataType) && (p.Min is not null || p.Max is not null))))
            .Select(c => $"{t.Key}.{c.Name}: samples [{string.Join(", ", c.Profile!.Samples)}] min {c.Profile.Min} max {c.Profile.Max}"))
        .ToList();

    [Fact]
    public void The_setting_round_trips_and_leaves_the_other_settings_alone()
    {
        using var project = TempProject.Create();
        var s = project.Services;
        s.Project.SaveSettings(s.Project.GetSettings() with { AutoAcceptScore = 0.9 });
        Assert.True(s.Project.GetSettings().SampleValues, "sample values are on by default");

        var off = SampleValuesSetting.Set(s, on: false);
        Assert.False(off.SampleValues);
        Assert.False(s.Project.GetSettings().SampleValues);

        var on = SampleValuesSetting.Set(s, on: true);
        Assert.True(on.SampleValues);
        Assert.Equal((true, 0.9), (s.Project.GetSettings().SampleValues, s.Project.GetSettings().AutoAcceptScore));
    }

    [Fact]
    public void Turning_it_off_removes_the_stored_sample_values_and_string_min_max_but_keeps_the_rest()
    {
        using var project = TempProject.Create();
        var s = project.Services.WithSampleCatalogs();
        var fingerprint = s.Catalog.Fingerprint(Side.Src);
        Assert.NotEmpty(Leaks(s));

        var change = SampleValuesSetting.Set(s, on: false);

        var leaks = Leaks(s);
        Assert.True(leaks.Count == 0, "with sample values off the stored catalogs still hold values Claude can read: " + string.Join("; ", leaks));
        Assert.True(change.ScrubbedColumns > 0);
        var custId = s.Catalog.Get(Side.Src)!.FindTable("dbo.CUST")!.FindColumn("CUST_ID")!.Profile!;
        Assert.Equal(("1", "1000", "integer"), (custId.Min, custId.Max, custId.SemanticClass));   // numeric range and classes stay
        Assert.Equal(fingerprint, s.Catalog.Fingerprint(Side.Src));                            // no false drift
        Assert.Contains(Profiles(s), p => p.SemanticClass == "email");
    }

    [Fact]
    public void With_sample_values_off_the_mapping_packet_carries_none()
    {
        using var project = TempProject.Create();
        var s = project.Services.WithSampleCatalogs();
        s.ApproveBefore(PhaseName.Mapping);
        s.AddMapping(AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions()), PhaseStatus.Drafting);
        Assert.Contains(Email, File.ReadAllText(s.Workflow.Next().Packet!));   // on: the packet shows samples

        SampleValuesSetting.Set(s, on: false);

        var packet = File.ReadAllText(s.Workflow.Next().Packet!);
        var samples = JsonNode.Parse(packet)!.ToJsonString().Contains("\"samples\"", StringComparison.Ordinal);
        Assert.False(packet.Contains(Email, StringComparison.Ordinal) || samples,
            $"with sample values off the mapping work packet still sends sample values to Claude (contains {Email}: "
            + $"{packet.Contains(Email, StringComparison.Ordinal)}, a \"samples\" key: {samples})");
    }

    /// <summary>Ruling 199 (fix round 1, F3): the values also sat in old work packets and in the SQLite write-ahead log.</summary>
    [Fact]
    public void Turning_it_off_leaves_no_sample_value_in_work_packets_or_the_database_files()
    {
        using var project = TempProject.Create();
        var s = project.Services.WithSampleCatalogs();
        s.ApproveBefore(PhaseName.Mapping);
        s.AddMapping(AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions()), PhaseStatus.Drafting);
        var packet = s.Workflow.Next().Packet!;
        File.WriteAllText(Path.Combine(project.Ws.WorkDir, "mapping-v0-draft.patch.json"), "{}");   // an agent's patch is left alone
        Assert.Contains(Email, File.ReadAllText(packet));

        SampleValuesSetting.Set(s, on: false);

        var files = Directory.EnumerateFiles(project.Ws.WorkDir, "*.json")
            .Concat(new[] { project.Ws.StateDbPath, project.Ws.StateDbPath + "-wal" }.Where(File.Exists));
        var leaks = files.Where(f => Contains(f, Email)).Select(Path.GetFileName).ToList();
        Assert.True(leaks.Count == 0, $"after switching sample values off, {Email} can still be read from: {string.Join(", ", leaks)}");
        Assert.False(File.Exists(packet), "the old work packet (written with sample values) must be deleted; dbm next writes it again");
        Assert.True(File.Exists(Path.Combine(project.Ws.WorkDir, "mapping-v0-draft.patch.json")));
        Assert.DoesNotContain(Email, File.ReadAllText(s.Workflow.Next().Packet!));
    }

    /// <summary>The bytes of a file another connection may hold open, searched as UTF-8.</summary>
    private static bool Contains(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray()).Contains(text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_discovery_that_finishes_while_it_is_off_is_cleaned_too()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.SaveConnections(s);                 // discover queued; the job read "on" when it started ...
        SampleValuesSetting.Set(s, on: false);           // ... the user switched it off meanwhile ...
        s.WithSampleCatalogs();                          // ... and the job saved catalogs with sample values

        FakeServices.CompleteNextJob(s);                 // discover done

        var leaks = Leaks(s);
        Assert.True(leaks.Count == 0, "a discovery finished while sample values were off left values Claude can read: " + string.Join("; ", leaks));
    }

    [Fact]
    public async Task Dbm_config_sample_values_switches_it_and_reports_the_state()
    {
        using var project = TempProject.Create();
        project.Services.WithSampleCatalogs();

        var off = await CliRunner.RunAsync(project.Ws, null, "config", "sample-values", "off");
        var show = await CliRunner.RunAsync(project.Ws, null, "show", "dbo.CUST", "--side", "src");
        var read = await CliRunner.RunAsync(project.Ws, null, "config", "sample-values");
        var on = await CliRunner.RunAsync(project.Ws, null, "config", "sample-values", "on");
        var bad = await CliRunner.RunAsync(project.Ws, null, "config", "sample-values", "maybe");

        Assert.True(off.Exit == 0 && off.Json["sampleValues"]!.GetValue<bool>() == false, off.Out + off.Err);
        Assert.False(show.Out.Contains(Email, StringComparison.Ordinal), "dbm show still prints a sample value after `config sample-values off`: " + show.Out);
        Assert.False(read.Json["sampleValues"]!.GetValue<bool>(), "dbm config sample-values reads on after switching off: " + read.Out);
        // Ruling 200: sending real values to Claude again is a human decision, taken on the Setup screen - never from the CLI,
        // which Claude itself runs.
        Assert.True(on.Exit == 1 && (string?)on.Json["error"] == "setup_screen_only" && on.Out.Contains("Privacy", StringComparison.Ordinal),
            "`dbm config sample-values on` must be refused and point to the Setup screen's Privacy card, got exit "
            + $"{on.Exit}: {on.Out}{on.Err}");
        Assert.False(project.Services.Project.GetSettings().SampleValues, "`dbm config sample-values on` switched sample values back on");
        Assert.Equal((1, "usage"), (bad.Exit, (string?)bad.Json["error"]));
    }
}
