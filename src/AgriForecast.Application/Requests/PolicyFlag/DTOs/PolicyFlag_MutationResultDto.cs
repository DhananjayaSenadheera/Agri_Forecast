namespace AgriForecast.Application.Requests.PolicyFlag.DTOs;

// Response for an update/delete. Carries the id of the affected row plus an OPTIONAL
// training-data warning: policy flags are as-of-joined into the ML model's training features,
// so mutating a flag whose effective window (or previous window) is in the past silently
// rewrites data the model has already learned from. The mutation still SUCCEEDS (owner wants
// warn, not block); when TrainingDataWarning is non-null the admin UI should surface it so an
// operator knows a retrain may be needed. Future-only windows => TrainingDataWarning is null.
public class PolicyFlag_MutationResultDto
{
    public Guid Id { get; set; }
    public string? TrainingDataWarning { get; set; }
}
