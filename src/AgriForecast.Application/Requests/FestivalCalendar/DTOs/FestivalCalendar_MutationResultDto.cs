namespace AgriForecast.Application.Requests.FestivalCalendar.DTOs;

// Response for an update/delete. Same shape/naming as PolicyFlag_MutationResultDto so the FE
// surfaces the warning identically: carries the id of the affected row plus an OPTIONAL
// training-data warning. Festival dates are as-of-joined into the ML model's training features
// (lead-up demand windows), so mutating a PAST-dated festival silently rewrites data the model
// has already learned from. The mutation still SUCCEEDS (warn, not block); when
// TrainingDataWarning is non-null the admin UI should surface it. Future-dated => null.
public class FestivalCalendar_MutationResultDto
{
    public Guid Id { get; set; }
    public string? TrainingDataWarning { get; set; }
}
