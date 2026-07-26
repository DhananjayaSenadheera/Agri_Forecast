using AgriForecast.Application.common;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services;

// Per-source run-row lifecycle wrapper for the Ingestion Worker. The Worker sequences the sources; this
// helper owns the audit around each one so the pattern lives in one tested place rather than being
// copy-pasted into every source block.
//
// Audit writes can NEVER break the pass: every audit DB write runs on its own isolated context (see
// IngestionRunRepository) and is wrapped so a failure is swallowed and logged. The source's own thrown
// exception is caught here too — this is the per-source fail-isolation belt — so one source never aborts
// the pass.
//
// A fail-safe source that swallows its own errors stays honest via IngestionRunStats.Outcome:
// Outcome=Failed or Skipped maps to a Failed or Skipped run row even though the body returned normally.
//
// CANCELLATION IS THE SHARP EDGE HERE. The admin stop button cancels the pass's token, so by the time a
// source unwinds, ct is already tripped. The TERMINAL write must therefore NOT observe ct: passing a
// cancelled token to the store makes SqlConnection.OpenAsync throw before any I/O, the swallow-and-log
// below hides it, and the row is stranded at Running/FinishedUtc=NULL. That single stranded row then reads
// as "running" for the full staleness window — Start answers 409 already_running and Stop answers
// not_stoppable for two hours after a stop that appeared to work. The Running INSERT may still honour ct
// (nothing is stranded if it never happens); the terminal transition may not.
public static class IngestionRunAudit
{
    // Recorded on the run row when an admin stop (or host shutdown) cut a source short. Kept distinct from
    // a generic failure so an operator is not sent hunting for an outage that never happened.
    public const string CancelledReason = "Cancelled by an admin stop request before the source completed.";
    // BatchId for a pass: read Ingestion:BatchId from config, or generate one if it is absent, empty or not a
    // GUID. Called once per pass by the Worker so every source row shares it.
    public static Guid ResolveBatchId(IConfiguration configuration)
    {
        var raw = configuration["Ingestion:BatchId"];
        return Guid.TryParse(raw, out var id) && id != Guid.Empty ? id : Guid.NewGuid();
    }

    // Runs one source under a tracked run row: insert Running (own commit), run the body, then the terminal
    // transition (own commit). The terminal state comes from the returned stats' Outcome, or Failed plus a
    // sanitized exception if the body throws. body returns null for a status-only source.
    //
    // RETURNS true when this source's own run row already carries the admin-stop cancellation (Failed +
    // CancelledReason). The caller uses that to decide whether the halt is already on the record: if it is,
    // the pass must NOT also mint a "cancelled before it started" marker for the next source, or one stop
    // would be reported twice. Note that a stop arriving mid-source does not always land here — a source can
    // finish successfully in the gap between the cancel and its next ct check — which is exactly why this is
    // reported from the row rather than inferred from ct.IsCancellationRequested.
    public static async Task<bool> RunTrackedAsync(
        IIngestionRunRepository runs,
        ILogger logger,
        Guid batchId,
        string source,
        Func<CancellationToken, Task<IngestionRunStats?>> body,
        CancellationToken ct)
    {
        // 1. Insert the Running row in its own commit so a crash mid-source leaves a null-FinishedUtc
        //    breadcrumb. If the audit write itself fails, log and carry on WITHOUT a run row — the source must
        //    still run, since the audit is best-effort.
        IngestionRun? run = null;
        try
        {
            run = IngestionRun.StartRunning(batchId, source, DateTime.UtcNow);
            await runs.AddAsync(run, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Ingestion-run audit: failed to record the Running row for {Source}; the source still runs (audit is best-effort).",
                source);
            run = null;
        }

        // 2. Run the source. A thrown exception is caught here (the fail-isolation belt) and maps to Failed.
        var cancellationRecorded = false;
        try
        {
            var stats = await body(ct);
            ApplyTerminal(logger, run, source, r => Transition(r, stats));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // An admin stop, not an outage. Terminal state is still Failed — the source did NOT succeed,
            // and mapping it to Skipped would be worse than useless: the status roll-up counts Skipped as
            // part of "succeeded", so a stopped pass would report a green lastRunStatus. Only the reason
            // differs, so the run row says what really happened.
            logger.LogInformation("{Source} ingestion was cancelled before it completed (admin stop).", source);
            ApplyTerminal(logger, run, source, r => r.MarkFailed(DateTime.UtcNow, CancelledReason));
            // Only claim the halt is on the record if there IS a row to carry it: when the Running insert
            // failed above, run is null and nothing was written, so the caller should still mint the marker.
            cancellationRecorded = run is not null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during {Source} ingestion", source);
            ApplyTerminal(logger, run, source, r => r.MarkFailed(DateTime.UtcNow, ex));
        }

        // 3. Persist the terminal transition on its own isolated commit, with CancellationToken.None — see
        //    the class comment: honouring a cancelled ct here strands the row at Running forever.
        await SaveTerminalAsync(runs, logger, run, source);

        return cancellationRecorded;
    }

    // Records a source that NEVER STARTED because the pass was stopped first: one already-terminal Failed row
    // carrying CancelledReason, inserted in a single commit (no Running phase — the source never ran, and a
    // Running breadcrumb here would read as a live pass for the whole staleness window).
    //
    // WHY A ROW AT ALL: a stop that lands in the gap between two sources used to write nothing, so a batch
    // whose finished sources had all succeeded rolled up to lastRunStatus="succeeded" — a deliberately halted
    // pass reporting green (observed live on batch 9def4f2b: stopped after DAMBULLA_DEC, before WEATHER).
    // Failed, never Skipped: the roll-up counts Skipped inside "succeeded", so a Skipped marker would leave
    // the false green exactly where it was.
    //
    // Takes NO CancellationToken, for the same reason SaveTerminalAsync does not: it is written precisely
    // because the pass's token is cancelled, and handing that token to the store makes the write throw
    // before any I/O.
    public static async Task RecordCancelledBeforeStartAsync(
        IIngestionRunRepository runs, ILogger logger, Guid batchId, string source)
    {
        try
        {
            var now = DateTime.UtcNow;
            var run = IngestionRun.StartRunning(batchId, source, now);
            run.MarkFailed(now, CancelledReason);
            await runs.AddAsync(run, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Best-effort like every other audit write: a failed marker must not break the shutdown path.
            logger.LogError(ex,
                "Ingestion-run audit: failed to record the stopped-before-start row for {Source}.", source);
        }
    }

    // Maps the source's returned stats to the terminal transition on the run row.
    private static void Transition(IngestionRun run, IngestionRunStats? stats)
    {
        var now = DateTime.UtcNow;
        switch (stats?.Outcome ?? IngestionRunOutcome.Succeeded)
        {
            case IngestionRunOutcome.Skipped:
                run.MarkSkipped(now);
                break;
            case IngestionRunOutcome.Failed:
                run.MarkFailed(now, stats?.FailureReason ?? "Source reported a failure.");
                break;
            default:
                run.MarkSucceeded(
                    now,
                    stats?.CoveredFromDate,
                    stats?.CoveredToDate,
                    stats?.RowsFetched,
                    stats?.RowsInserted,
                    stats?.RowsSkipped,
                    stats?.DistinctCrops);
                break;
        }
    }

    // Applies the terminal transition to the detached run row in memory; the save follows. A no-op if the
    // Running row was never recorded.
    private static void ApplyTerminal(ILogger logger, IngestionRun? run, string source, Action<IngestionRun> transition)
    {
        if (run is null) return;
        try
        {
            transition(run);
        }
        catch (Exception ex)
        {
            // A transition that throws (should not happen) must not break the pass.
            logger.LogError(ex, "Ingestion-run audit: failed to apply the terminal transition for {Source}.", source);
        }
    }

    // Persists the terminal transition on its own isolated context. Swallow and log so a failed audit write
    // never breaks the pass.
    //
    // Deliberately takes NO CancellationToken: this write must happen precisely in the case where the pass's
    // token is already cancelled. It is a short, bounded UPDATE of one row, so it is safe to let it run to
    // completion; a stranded Running row is far more damaging than a few extra milliseconds on shutdown.
    private static async Task SaveTerminalAsync(
        IIngestionRunRepository runs, ILogger logger, IngestionRun? run, string source)
    {
        if (run is null) return;
        try
        {
            await runs.UpdateAsync(run, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Ingestion-run audit: failed to persist the terminal ({Status}) row for {Source}; the pass continues.",
                run.Status, source);
        }
    }
}
