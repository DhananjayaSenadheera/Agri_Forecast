namespace AgriForecast.Application.Requests.PolicyFlag.DTOs;

// Response for an update or delete: the affected id plus an optional training-data warning. Policy flags
// feed ML training features, so mutating a flag whose window has already started rewrites data the model
// learned from. The mutation still succeeds — the warning is for the admin UI to surface.
public class PolicyFlag_MutationResultDto
{
    public Guid Id { get; set; }
    public string? TrainingDataWarning { get; set; }
}
