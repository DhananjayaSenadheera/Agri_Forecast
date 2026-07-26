namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetTrainingRuns;

// One model-training run for GET /api/admin/logs/training. promoted and decisionPromoted differ on a
// manual override, so both are surfaced. MAE fields are nullable — a run may not report them.
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
