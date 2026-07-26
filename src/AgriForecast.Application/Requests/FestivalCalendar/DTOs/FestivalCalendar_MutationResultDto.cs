namespace AgriForecast.Application.Requests.FestivalCalendar.DTOs;

// Response for an update or delete: the affected id plus an optional training-data warning. Festival
// dates feed ML training features, so mutating a past-dated festival rewrites data the model already
// learned from. The mutation still succeeds — the warning is for the admin UI to surface.
public class FestivalCalendar_MutationResultDto
{
    public Guid Id { get; set; }
    public string? TrainingDataWarning { get; set; }
}
