namespace AgriForecast.Application.Requests.PolicyFlag;

// The "you are editing training history" warning for policy flags.
//
// Flags are as-of-joined into ML training features by EffectiveFrom/EffectiveTo, so a flag whose window
// has already started has been baked into whatever the model last trained on. Editing or deleting one
// warns; it never blocks.
// Past means EffectiveFrom is strictly before today's UTC date — EffectiveTo does not narrow this, since
// once a window has begun it has touched history. On update both the incoming and the previous window
// are considered. An entirely future window produces no warning.
public static class PolicyFlagTrainingDataWarning
{
    public const string Message =
        "This policy flag's effective window falls (partly) in the past. Policy flags are as-of-joined " +
        "into the forecasting model's training data, so editing or removing a past-dated flag changes " +
        "history the model has already learned from — a retrain may be required.";

    // previousEffectiveFrom: the stored value before the edit. Pass the same value for a delete, or null
    // when there is no prior window.
    public static string? For(DateTime effectiveFrom, DateTime? previousEffectiveFrom, DateTime nowUtc)
    {
        var today = nowUtc.Date;
        var touchesPast =
            effectiveFrom.Date < today ||
            (previousEffectiveFrom.HasValue && previousEffectiveFrom.Value.Date < today);

        return touchesPast ? Message : null;
    }
}
