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
public static class IngestionRunAudit
{
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
    public static async Task RunTrackedAsync(
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
        try
        {
            var stats = await body(ct);
            ApplyTerminal(logger, run, source, r => Transition(r, stats));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during {Source} ingestion", source);
            ApplyTerminal(logger, run, source, r => r.MarkFailed(DateTime.UtcNow, ex));
        }

        // 3. Persist the terminal transition on its own isolated commit.
        await SaveTerminalAsync(runs, logger, run, source, ct);
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
    private static async Task SaveTerminalAsync(
        IIngestionRunRepository runs, ILogger logger, IngestionRun? run, string source, CancellationToken ct)
    {
        if (run is null) return;
        try
        {
            await runs.UpdateAsync(run, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Ingestion-run audit: failed to persist the terminal ({Status}) row for {Source}; the pass continues.",
                run.Status, source);
        }
    }
}
