using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.PolicyFlag.DTOs;

public class PolicyFlag_GetDto
{
    public Guid Id { get; set; }
    public PolicyType PolicyType { get; set; }
    public string Title { get; set; }
    public string? Description { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public PolicyDirection Direction { get; set; }
    public string? Source { get; set; }
    public string? ReferenceUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
