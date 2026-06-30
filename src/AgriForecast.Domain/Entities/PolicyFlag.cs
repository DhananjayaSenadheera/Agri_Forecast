using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// A government policy captured as a point-in-time flag for the ML feature store.
// EffectiveFrom / EffectiveTo are the point-in-time keys the ML layer as-of-joins on:
// a flag is "active" on date D when EffectiveFrom <= D and (EffectiveTo is null or D <= EffectiveTo).
// Dates are stored date-only (no time component) to avoid any hidden "now" leakage.
public class PolicyFlag
{
    public Guid Id { get; set; }

    public PolicyType PolicyType { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Point-in-time validity window. EffectiveTo == null => still in effect.
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    // Coarse expected price impact — the signal the ML layer reads.
    public PolicyDirection Direction { get; set; }

    // Provenance.
    public string? Source { get; set; }
    public string? ReferenceUrl { get; set; }

    // Audit: when the row was recorded (record-keeping only; never used as a feature).
    public DateTime CreatedAtUtc { get; set; }
}
