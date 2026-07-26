using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.PolicyFlag.DTOs;

// Full-object update: the create shape plus the Id. The admin console sends the whole flag back.
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

    // Required on mutation, unlike create: every edit to training-relevant data carries a citation.
    public string? Source { get; set; }
    public string? ReferenceUrl { get; set; }
}
