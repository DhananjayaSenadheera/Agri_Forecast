namespace AgriForecast.Application.Requests.PolicyFlag;

// Single source of truth for the "you are editing training history" warning.
//
// Policy flags are as-of-joined into the ML model's training features by EffectiveFrom/EffectiveTo.
// A flag whose window has already STARTED (EffectiveFrom is before today) has therefore already been
// baked into whatever the model last trained on. Editing or deleting such a flag changes that history
// silently — so we WARN (never block: the owner wants the mutation to succeed and the admin to see it).
//
// Semantics (documented, load-bearing):
//   * "Past" = EffectiveFrom is strictly before today's UTC date. (An open-ended flag with EffectiveTo
//     null that started in the past is still past; EffectiveTo does not narrow this — once a window has
//     begun it has touched history.)
//   * On UPDATE we consider BOTH the incoming window and the previous (stored) window: moving a flag
//     OUT of the past, or INTO the past, both change training history, so either being past warns.
//   * A window that is entirely in the future (EffectiveFrom today or later, and no past previous
//     window) has not yet fed any training run => no warning (null).
public static class PolicyFlagTrainingDataWarning
{
    public const string Message =
        "This policy flag's effective window falls (partly) in the past. Policy flags are as-of-joined " +
        "into the forecasting model's training data, so editing or removing a past-dated flag changes " +
        "history the model has already learned from — a retrain may be required.";

    // previousEffectiveFrom: the stored EffectiveFrom before the edit (pass the same value as
    // effectiveFrom for a delete, or null when there is no prior window to consider).
    public static string? For(DateTime effectiveFrom, DateTime? previousEffectiveFrom, DateTime nowUtc)
    {
        var today = nowUtc.Date;
        var touchesPast =
            effectiveFrom.Date < today ||
            (previousEffectiveFrom.HasValue && previousEffectiveFrom.Value.Date < today);

        return touchesPast ? Message : null;
    }
}
