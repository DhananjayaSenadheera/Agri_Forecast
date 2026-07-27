using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Common;

// The one definition of "how did this batch end", shared by the admin ingestion status card and the
// pipeline health endpoint. Both answer the same question off the same rows, so a second copy of these
// rules would eventually drift and let one screen call a night green while the other calls it partial.
public static class IngestionBatchRollup
{
    // All Succeeded/Skipped -> "succeeded" (a Skipped source is a deliberate no-op, not a failure);
    // some Failed -> "partial"; all Failed -> "failed"; a Running row with no failures -> "partial",
    // never a premature "succeeded". Null when there are no rows to roll up.
    public static string? Aggregate(IReadOnlyList<IngestionRunStatus> statuses)
    {
        if (statuses.Count == 0) return null;

        var anyFailed = statuses.Any(s => s == IngestionRunStatus.Failed);
        if (anyFailed)
            return statuses.All(s => s == IngestionRunStatus.Failed) ? Failed : Partial;

        var allGood = statuses.All(s =>
            s == IngestionRunStatus.Succeeded || s == IngestionRunStatus.Skipped);
        return allGood ? Succeeded : Partial;
    }

    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Failed = "failed";
}
