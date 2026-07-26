namespace AgriForecast.Domain.Enums;

// Outcome of a post-pass data-quality verification run. Stored as int — the numeric values are
// persisted, so never renumber them. Rows are written by the Python side; .NET owns the schema.
// Pass = every check passed. Warn = at least one WARN and no FAIL. Fail = at least one FAIL.
public enum IngestionVerificationStatus
{
    Pass = 0,
    Warn = 1,
    Fail = 2
}
