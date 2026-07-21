namespace AgriForecast.Domain.Enums;

// Overall outcome of a post-pass data-quality VERIFICATION run. Stored as int
// (HasConversion<int>); the numeric values are pinned by test — a reorder would silently
// corrupt persisted rows (fix a reorder in a migration, never here).
//
// The verification rows are WRITTEN by the Python side later (this PR owns the schema only);
// .NET defines the enum so the column type + values are the single source of truth for both
// languages.
//
// Pass = every check passed. Warn = at least one WARN check but no FAIL. Fail = at least one
// FAIL check (the pass produced data a downstream consumer must not trust blindly).
public enum IngestionVerificationStatus
{
    Pass = 0,
    Warn = 1,
    Fail = 2
}
