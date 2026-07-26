namespace AgriForecast.Domain.Entities;

// One row per model training run. Rows are written by the Python training pipeline; .NET owns the schema
// and the admin Logs hub reads them. No .NET code path inserts or updates this table.
//
// Promoted is the current live-pointer bit, including a human override; DecisionPromoted is the automated
// gate's verdict at train time. They differ when a version is promoted despite the gate.
public class ModelTrainingRun
{
    public int Id { get; private set; }

    // The model version string this run produced (e.g. "v17"). Unique across the table.
    public string Version { get; private set; } = string.Empty;

    public DateTime TrainedAtUtc { get; private set; }

    // Is this version the CURRENT live pointer (maintained by Python; updated on promote/override).
    public bool Promoted { get; private set; }

    // The automated gate's verdict at train time (may DIFFER from Promoted on a manual override).
    public bool DecisionPromoted { get; private set; }

    // Sanitized free-text rationale for the promote/hold decision (Python-authored).
    public string? PromotionDecision { get; private set; }

    // Winning learner and baseline with their holdout MAEs. All nullable — a run may not report them.
    public string? BestMlKind { get; private set; }
    public decimal? BestMlMae { get; private set; }
    public string? BestBaselineKind { get; private set; }
    public decimal? BestBaselineMae { get; private set; }

    public int? NTrainRows { get; private set; }
    public int? NCrops { get; private set; }

    // Hash of the feature contract the run trained against (drift/repro aid).
    public string? FeatureContractHash { get; private set; }

    // Record-keeping only; never a feature.
    public DateTime CreatedUtc { get; private set; }

    private ModelTrainingRun() { }

    // For tests and future .NET reads. createdUtc is passed in so tests are deterministic; in production
    // the DB default fills it.
    public static ModelTrainingRun Create(
        string version,
        DateTime trainedAtUtc,
        bool promoted,
        bool decisionPromoted,
        DateTime createdUtc,
        string? promotionDecision = null,
        string? bestMlKind = null,
        decimal? bestMlMae = null,
        string? bestBaselineKind = null,
        decimal? bestBaselineMae = null,
        int? nTrainRows = null,
        int? nCrops = null,
        string? featureContractHash = null)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Version is required.", nameof(version));

        return new ModelTrainingRun
        {
            Version = version,
            TrainedAtUtc = trainedAtUtc,
            Promoted = promoted,
            DecisionPromoted = decisionPromoted,
            PromotionDecision = promotionDecision,
            BestMlKind = bestMlKind,
            BestMlMae = bestMlMae,
            BestBaselineKind = bestBaselineKind,
            BestBaselineMae = bestBaselineMae,
            NTrainRows = nTrainRows,
            NCrops = nCrops,
            FeatureContractHash = featureContractHash,
            CreatedUtc = createdUtc
        };
    }
}
