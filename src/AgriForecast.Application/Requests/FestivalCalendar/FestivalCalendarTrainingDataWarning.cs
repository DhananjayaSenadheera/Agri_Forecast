namespace AgriForecast.Application.Requests.FestivalCalendar;

// Single source of truth for the "you are editing training history" warning — the festival
// parallel of PolicyFlagTrainingDataWarning (same shape, same For(...) signature, festival-
// specific message), so the FE reads the identical { id, trainingDataWarning } contract.
//
// Festival dates are as-of-joined into the ML model's training features: each festival contributes
// a lead-up demand window [Date - LeadUpDays, Date] (and a "days to next festival" countdown). A
// festival whose Date is already in the PAST has therefore already been baked into whatever the
// model last trained on. Editing or deleting such a row changes that history silently — so we WARN
// (never block: the owner wants the mutation to succeed and the admin to see it).
//
// Semantics (documented, load-bearing):
//   * "Past" = the festival Date is strictly before today's UTC date.
//   * On UPDATE we consider BOTH the incoming Date and the previous (stored) Date: moving a
//     festival OUT of the past, or INTO the past, both change training history, so either being
//     past warns.
//   * On DELETE the stored Date is passed as both arguments.
//   * A purely future-dated festival (Date today or later, and no past previous Date) has not yet
//     fed any training run => no warning (null).
public static class FestivalCalendarTrainingDataWarning
{
    public const string Message =
        "This festival's date falls in the past. Festival dates are as-of-joined into the " +
        "forecasting model's training data (lead-up demand windows), so editing or removing a " +
        "past-dated festival changes history the model has already learned from — a retrain may " +
        "be required.";

    // previousDate: the stored Date before the edit (pass the same value as date for a delete, or
    // null when there is no prior row to consider — e.g. a create).
    public static string? For(DateTime date, DateTime? previousDate, DateTime nowUtc)
    {
        var today = nowUtc.Date;
        var touchesPast =
            date.Date < today ||
            (previousDate.HasValue && previousDate.Value.Date < today);

        return touchesPast ? Message : null;
    }
}
