using Dbm.Core.Sql;
using Dbm.Core.State;

namespace Dbm.Core.Jobs;

/// <summary>Runs queued jobs one at a time. Safe to run in several processes: jobs are claimed atomically.</summary>
public sealed class JobRunner(DbmServices services)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Runs queued jobs until none is left; returns how many ran (used by tests and `dbm run-jobs`).</summary>
    public async Task<int> RunPendingAsync(CancellationToken ct)
    {
        var count = 0;
        while (!ct.IsCancellationRequested)
        {
            var next = services.Jobs.NextQueued();
            if (next is null) break;
            if (!services.Jobs.TryClaim(next.Id)) continue;
            await RunOneAsync(services.Jobs.Get(next.Id)!, ct);
            count++;
        }
        return count;
    }

    /// <summary>Server loop: jobs left "running" by a dead process are re-queued, then the queue is polled.</summary>
    public async Task RunLoopAsync(CancellationToken ct)
    {
        services.Jobs.RequeueStaleRunning();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunPendingAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                services.Sink.Publish("log", new { level = "error", message = $"job runner: {Scrub(ex.Message)}" });
            }
            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOneAsync(JobRow job, CancellationToken ct)
    {
        services.Sink.Publish("job_started", new { id = job.Id, kind = job.Kind, phase = job.Phase });
        try
        {
            if (!services.JobHandlers.TryGetValue(job.Kind, out var handler))
                throw new InvalidOperationException($"no handler for {job.Kind}");

            var ctx = new JobContext
            {
                Services = services,
                Job = job,
                Log = message => services.Sink.Publish("log", new { level = "info", message = Scrub(message), jobId = job.Id }),
            };
            var result = await handler.RunAsync(ctx, ct);
            services.Db.InTransaction(() =>
            {
                services.Jobs.MarkDone(job.Id);
                services.Workflow.OnJobDone(job, result.DraftPayload, result.Summary);
            });
            services.Sink.Publish("job_done", new { id = job.Id, kind = job.Kind, phase = job.Phase });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Server shutting down: leave the job "running"; the next server start re-queues it.
            throw;
        }
        catch (Exception ex)
        {
            var message = Scrub(ex.Message);
            services.Jobs.MarkFailed(job.Id, message);
            services.Workflow.OnJobFailed(job, message);
        }
    }

    /// <summary>Removes any password/secret of the saved connections from a message.</summary>
    private string Scrub(string message)
    {
        var secrets = new List<string?>();
        foreach (var side in new[] { Side.Src, Side.Tgt })
        {
            try
            {
                var cs = services.Connections.GetConnectionString(side);
                if (cs is not null) secrets.AddRange(Redactor.SecretsOf(cs));
            }
            catch (Exception)
            {
                // unreadable secret: nothing to scrub for this side
            }
        }
        return Redactor.Scrub(message, secrets);
    }
}
