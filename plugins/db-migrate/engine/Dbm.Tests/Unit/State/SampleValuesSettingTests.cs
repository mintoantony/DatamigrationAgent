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
        Assert.True(on.Exit == 0 && on.Json["sampleValues"]!.GetValue<bool>(), on.Out + on.Err);
        Assert.True(project.Services.Project.GetSettings().SampleValues);
        Assert.Equal((1, "usage"), (bad.Exit, (string?)bad.Json["error"]));
    }
}
