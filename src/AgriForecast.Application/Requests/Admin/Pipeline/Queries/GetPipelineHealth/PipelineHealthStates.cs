namespace AgriForecast.Application.Requests.Admin.Pipeline.Queries.GetPipelineHealth;

// The six values PipelineHealth_GetDto.State can take. Exactly six — the set is pinned with the FE
// banner, so a new state is a contract change on both sides, not a one-line addition here.
public static class PipelineHealthStates
{
    // Ran, finished, verification did not fail, and the feature store was rebuilt.
    public const string Green = "green";

    // A batch exists for this night and something in it is still in flight (and fresh).
    public const string Running = "running";

    // Some sources landed, some failed. Data arrived, but not all of it.
    public const string Partial = "partial";

    // Every source failed, or the night finished without the feature build that is the point of it.
    public const string Failed = "failed";

    // The verification gate returned Fail, so the pipeline stopped before news/sentiment/features.
    public const string GateBlocked = "gate_blocked";

    // Nothing ran in this night's window at all. The state that catches a suspended CronJob.
    public const string Missing = "missing";
}
