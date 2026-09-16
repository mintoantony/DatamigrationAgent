using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class TransferProgressTests
{
    private DateTimeOffset _now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static TransferTaskRow Row(string id, string target, long? source) =>
        new(1, id, target, id == "T01" ? 0 : 1, TransferTaskStatus.Pending, source, 0, 0, 0, null, null, null, null, null, null);

    [Fact]
    public void RateWindow_measures_rows_per_second_over_the_window()
    {
        var w = new RateWindow(TimeSpan.FromSeconds(10));
        Assert.Equal(0, w.PerSecond);
        w.Add(_now, 0);
        w.Add(_now.AddSeconds(2), 1000);
        Assert.Equal(500, w.PerSecond);
        w.Add(_now.AddSeconds(30), 1000);          // old samples fall out; no progress -> 0
        w.Add(_now.AddSeconds(31), 1000);
        Assert.Equal(0, w.PerSecond);
    }

    [Fact]
    public void Snapshot_aggregates_committed_and_in_flight_rows_with_eta()
    {
        var sink = new RecordingSink();
        var p = new TransferProgress(7, sink, [Row("T01", "app.A", 1000), Row("T02", "app.B", 2000)], () => _now);
        p.SetStatus("T01", TransferTaskStatus.Running);
        p.Committed("T01", 0, 0);
        _now = _now.AddSeconds(2);
        p.Committed("T01", 800, 3);
        p.InFlight("T01", 200);
        var s = p.Snapshot();
        Assert.Equal(7, s.RunId);
        Assert.Equal("running", s.Status);
        var t1 = s.Tasks.Single(t => t.TaskId == "T01");
        Assert.Equal(1000, t1.RowsDone);
        Assert.Equal(3, t1.RowsError);
        Assert.Equal("running", t1.Status);
        Assert.Equal(500, t1.RowsPerSec);
        Assert.Equal(3000, s.Overall.Total);
        Assert.Equal(1000, s.Overall.Done);
        Assert.Equal(500, s.Overall.RowsPerSec);
        Assert.Equal(4, s.Overall.EtaSec);                         // (3000 - 1000) / 500
        Assert.Equal("pending", s.Tasks.Single(t => t.TaskId == "T02").Status);
    }

    [Fact]
    public void Publish_is_throttled_to_four_per_second_unless_forced()
    {
        var sink = new RecordingSink();
        var p = new TransferProgress(1, sink, [Row("T01", "app.A", 10)], () => _now);
        Assert.True(p.Publish(false));
        _now = _now.AddMilliseconds(100);
        Assert.False(p.Publish(false));
        Assert.True(p.Publish(true));
        _now = _now.AddMilliseconds(300);
        Assert.True(p.Publish(false));
        Assert.Equal(3, sink.Count("transfer_progress"));
        Assert.All(sink.Events, e => Assert.False(e.Persist));
    }

    [Fact]
    public void Control_precedence_is_fail_over_cancel_over_pause()
    {
        var c = new TransferControl();
        Assert.False(c.StopRequested);
        c.RequestPause();
        Assert.Equal(StopKind.Pause, c.Kind);
        c.RequestCancel();
        Assert.Equal(StopKind.Cancel, c.Kind);
        c.RequestPause();
        Assert.Equal(StopKind.Cancel, c.Kind);
        c.RequestFail();
        c.RequestCancel();
        Assert.Equal(StopKind.Fail, c.Kind);
    }

    /// <summary>
    /// Harm: a total built from "?? 0" for a task whose source has not been counted is smaller than the real work, so the ETA derived
    /// from it is a number that says the run is nearly over while a whole table is still unread. A count of 0 that means "never looked"
    /// must not be spent as if it meant "none".
    /// </summary>
    [Fact]
    public void Eta_is_withheld_while_any_task_source_count_is_still_unknown()
    {
        var sink = new RecordingSink();
        var p = new TransferProgress(3, sink, [Row("T01", "app.A", 1000), Row("T02", "app.B", null)], () => _now);
        p.SetStatus("T01", TransferTaskStatus.Running);
        p.Committed("T01", 0, 0);
        _now = _now.AddSeconds(2);
        p.Committed("T01", 1000, 0);

        var s = p.Snapshot();
        Assert.Equal(500, s.Overall.RowsPerSec);
        Assert.Equal(1, s.Overall.TasksWithoutSource);          // says why the total is short
        Assert.Null(s.Overall.EtaSec);                          // and refuses to price the remaining work from it
        Assert.Null(s.Tasks.Single(t => t.TaskId == "T02").RowsSource);

        p.SetSource("T02", 3000);
        var known = p.Snapshot();
        Assert.Equal(0, known.Overall.TasksWithoutSource);
        Assert.Equal(6, known.Overall.EtaSec);                  // (4000 - 1000) / 500
    }
}
