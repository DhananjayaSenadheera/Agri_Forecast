namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetTrainingRuns;

// One model-training run for GET /api/admin/logs/training. promoted vs decisionPromoted DIFFER on a
// manual override (e.g. v17: decisionPromoted=false, promoted=true) — both surfaced so the hub can
// show "promoted despite the gate". MAE fields are nullable (a run may not report them). PascalCase
// -> camelCase JSON per the API default policy.
public class TrainingRun_GetDto
{
    public string Version { get; set; } = string.Empty;
    public DateTime TrainedAtUtc { get; set; }
    public bool Promoted { get; set; }
    public bool DecisionPromoted { get; set; }
    public string? PromotionDecision { get; set; }
    public string? BestMlKind { get; set; }
    public decimal? BestMlMae { get; set; }
    public string? BestBaselineKind { get; set; }
    public decimal? BestBaselineMae { get; set; }
    public int? NTrainRows { get; set; }
    public int? NCrops { get; set; }
}
