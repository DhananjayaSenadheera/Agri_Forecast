using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.PolicyFlag.DTOs;

public class PolicyFlag_CreateDto
{
    public PolicyType PolicyType { get; set; }
    public string Title { get; set; }
    public string? Description { get; set; }

    // Date-only on the wire; the boundary keeps it date-only in storage too.
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    public PolicyDirection Direction { get; set; }
    public string? Source { get; set; }
    public string? ReferenceUrl { get; set; }
}
