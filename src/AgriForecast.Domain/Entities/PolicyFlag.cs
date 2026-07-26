using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// A government policy as a point-in-time flag for the ML feature store. A flag is active on date D when
// EffectiveFrom <= D and (EffectiveTo is null or D <= EffectiveTo). Dates are stored date-only.
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

    public string? Source { get; set; }
    public string? ReferenceUrl { get; set; }

    // Record-keeping only; never used as a feature.
    public DateTime CreatedAtUtc { get; set; }
}
