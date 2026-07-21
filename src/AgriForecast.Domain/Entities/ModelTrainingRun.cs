namespace AgriForecast.Domain.Entities;

// One row per model TRAINING run -> table ModelTrainingRuns (Logs hub PR A). WRITTEN BY THE PYTHON
// side (the training pipeline INSERTs a row at train time and UPDATEs Promoted on a promote/override)
// — a sanctioned Python-writes / .NET-reads table, exactly like IngestionVerifications. This .NET
// entity owns the SCHEMA only (so the migration + column types are the single source of truth for
// both languages); NO .NET code path inserts or updates it, and no repository writes it. The admin
// Logs hub (AdminLogsController) READS it.
//
// Promoted vs DecisionPromoted DIFFER on a manual override: Promoted is the CURRENT live-pointer bit
// (maintained by Python — did this version end up promoted, incl. a human override), while
// DecisionPromoted is the automated gate's verdict AT TRAIN TIME. v17 is the canonical example: the
// gate said no (DecisionPromoted = false) but it was promoted by override (Promoted = true). Surfacing
// both lets the hub show "promoted despite the gate".
//
// A Create factory is provided for completeness / tests / future .NET reads (mirrors
// IngestionVerification.Create); the row is otherwise Python-authored. MAE columns are decimal(10,2)
// (explicit precision, house discipline); CreatedUtc defaults to SYSUTCDATETIME() at the DB.
public class ModelTrainingRun
{
    // int identity (DB-generated).
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

    // Winning ML learner + its holdout MAE, and the winning baseline + its MAE (for "did the model
    // actually beat the baseline" at a glance). All nullable — a run may not report them.
    public string? BestMlKind { get; private set; }
    public decimal? BestMlMae { get; private set; }
    public string? BestBaselineKind { get; private set; }
    public decimal? BestBaselineMae { get; private set; }

    public int? NTrainRows { get; private set; }
    public int? NCrops { get; private set; }

    // Hash of the feature contract the run trained against (drift/repro aid).
    public string? FeatureContractHash { get; private set; }

    // Record-keeping only (row creation instant, DB-defaulted); never a feature.
    public DateTime CreatedUtc { get; private set; }

    private ModelTrainingRun() { }

    // Factory (completeness / tests / future .NET reads). The row is normally Python-authored; .NET
    // never inserts it in production. createdUtc is passed in (never an internal UtcNow) so tests are
    // deterministic — in production the DB SYSUTCDATETIME() default fills it.
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
