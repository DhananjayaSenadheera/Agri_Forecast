using AgriForecast.Application.Requests.FestivalCalendar.Commands.Create;
using FluentValidation;

namespace AgriForecast.Application.Requests.FestivalCalendar.Validators;

// The ML model TRAINS on this table, so these rules protect what the feature layer reads:
//   * FestivalKey — required, <= 50 (entity max), and UPPERCASE [A-Z0-9_]. The Python feature
//     layer (features.py _festival_windows / _LEADUP_FESTIVALS) string-matches the key
//     case-sensitively for per-festival columns (InLeadupAvurudu / InLeadupChristmas); a
//     lowercase key would silently miss that column (same class of bug as an uppercase GUID
//     missing the model's per-crop fallback). It is still an OPEN set — any uppercase key is
//     accepted, so a new festival is just a new row, never a code change.
//   * Date — required. Kept date-only in storage by the mapper (.Date); it is the point-in-time
//     ML as-of-join key and must never carry a hidden time (leakage guard).
//   * LeadUpDays — >= 0, NOT > 0. Zero is the PAIRED-DAY convention: a multi-day festival
//     (verified in live data: AVURUDU, stored as Apr 13 + Apr 14) carries the lead-up window on
//     its anchor row (LeadUpDays=14) and 0 on the continuation row so the demand window is not
//     double-counted (features.py only opens a window for LeadUpDays > 0). Upper bound 90 is a
//     sanity ceiling only — the ML imposes no hard cap (the countdown clip of 30 days is a
//     separate concern), but a lead-up longer than a season is almost certainly a typo.
//   * IsProvisional — bool, passed straight through (no rule; never silently upgraded).
//   * Source — REQUIRED on create (stricter than PolicyFlag): festivals are curated data, so
//     every row carries a gazette citation.
public class FestivalCalendarCreateCommandValidator : AbstractValidator<FestivalCalendarCreateCommand>
{
    public FestivalCalendarCreateCommandValidator()
    {
        RuleFor(x => x.FestivalCalendarCreateDto).NotNull().WithMessage("Festival details are required.");

        RuleFor(x => x.FestivalCalendarCreateDto.FestivalKey)
            .NotEmpty().WithMessage("FestivalKey is required.")
            .MaximumLength(50).WithMessage("FestivalKey cannot exceed 50 characters.")
            .Matches("^[A-Z0-9_]+$").WithMessage(
                "FestivalKey must be uppercase letters, digits or underscore (e.g. AVURUDU, THAI_PONGAL).");

        RuleFor(x => x.FestivalCalendarCreateDto.Date)
            .NotEmpty().WithMessage("Date is required.");

        RuleFor(x => x.FestivalCalendarCreateDto.LeadUpDays)
            .GreaterThanOrEqualTo(0).WithMessage("LeadUpDays cannot be negative.")
            .LessThanOrEqualTo(90).WithMessage("LeadUpDays cannot exceed 90.");

        RuleFor(x => x.FestivalCalendarCreateDto.Source)
            .NotEmpty().WithMessage("Source is required when creating a festival.")
            .MaximumLength(300).WithMessage("Source cannot exceed 300 characters.");
    }
}
