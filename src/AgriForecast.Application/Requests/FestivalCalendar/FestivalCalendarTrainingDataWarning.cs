namespace AgriForecast.Application.Requests.FestivalCalendar;

// The "you are editing training history" warning for festivals — the parallel of
// PolicyFlagTrainingDataWarning, so the FE reads the identical { id, trainingDataWarning } contract.
//
// Festival dates feed the model's lead-up demand windows, so a festival already in the past has been
// baked into whatever the model last trained on. Editing or deleting one warns; it never blocks.
// Past means strictly before today's UTC date. On update both the incoming and the previous date are
// considered, since moving a festival into or out of the past both change history; on delete the stored
// date is passed twice. A purely future-dated festival produces no warning.
public static class FestivalCalendarTrainingDataWarning
{
    public const string Message =
        "This festival's date falls in the past. Festival dates are as-of-joined into the " +
        "forecasting model's training data (lead-up demand windows), so editing or removing a " +
        "past-dated festival changes history the model has already learned from — a retrain may " +
        "be required.";

    // previousDate: the stored Date before the edit. Pass the same value for a delete, or null on create.
    public static string? For(DateTime date, DateTime? previousDate, DateTime nowUtc)
    {
        var today = nowUtc.Date;
        var touchesPast =
            date.Date < today ||
            (previousDate.HasValue && previousDate.Value.Date < today);

        return touchesPast ? Message : null;
    }
}
