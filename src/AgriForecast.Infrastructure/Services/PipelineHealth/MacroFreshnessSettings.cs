using System.Globalization;
using AgriForecast.Application.Services;
using Microsoft.Extensions.Configuration;

namespace AgriForecast.Infrastructure.Services.PipelineHealth;

// Resolves the "MacroFreshness" section, applying the documented fallbacks. Same shape as
// PipelineScheduleSettings / SentinelSettings next to it: read the RAW string and TryParse, never
// GetValue<T>, so a typo in a config value cannot throw at construction — that would be a 500 on the very
// endpoint whose job is to report trouble.
public class MacroFreshnessSettings : IMacroFreshnessSettings
{
    private const string StaleAfterKey = "MacroFreshness:StaleAfterDays";
    private const string RepeatKey = "MacroFreshness:AlertRepeatDays";

    // See IMacroFreshnessSettings for the arithmetic behind 40: above the ~37-day worst normal cycle,
    // below two cycles.
    private const int DefaultStaleAfterDays = 40;
    private const int DefaultAlertRepeatDays = 7;

    public int StaleAfterDays { get; }
    public int AlertRepeatDays { get; }

    public MacroFreshnessSettings(IConfiguration configuration)
    {
        StaleAfterDays = ReadPositiveInt(configuration[StaleAfterKey], DefaultStaleAfterDays);
        AlertRepeatDays = ReadPositiveInt(configuration[RepeatKey], DefaultAlertRepeatDays);
    }

    // Zero and negatives fall back to the default rather than being honoured. A StaleAfterDays of 0 would
    // call every database stale forever, and an AlertRepeatDays of 0 would mail nightly — both are far
    // more likely to be a bad config value than a deliberate choice, and both destroy the alert's
    // credibility rather than merely mis-tuning it.
    private static int ReadPositiveInt(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
}
