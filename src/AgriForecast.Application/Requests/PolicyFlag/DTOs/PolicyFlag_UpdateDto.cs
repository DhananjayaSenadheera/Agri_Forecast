using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.PolicyFlag.DTOs;

// Full-object update: mirrors PolicyFlag_CreateDto plus the Id of the row being edited.
// The admin console sends the whole flag back (not a patch), so every field is replaced.
public class PolicyFlag_UpdateDto
{
    public Guid Id { get; set; }

    public PolicyType PolicyType { get; set; }
    public string Title { get; set; }
    public string? Description { get; set; }

    // Date-only on the wire; the boundary keeps it date-only in storage too.
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    public PolicyDirection Direction { get; set; }

    // Required on mutation (not just create): policy flags are as-of-joined into ML training
    // data, so every edit must carry a citation for provenance. Enforced by the validator.
    public string? Source { get; set; }
    public string? ReferenceUrl { get; set; }
}
