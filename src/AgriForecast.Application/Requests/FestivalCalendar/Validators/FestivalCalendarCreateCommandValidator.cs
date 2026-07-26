using AgriForecast.Application.Requests.FestivalCalendar.Commands.Create;
using FluentValidation;

namespace AgriForecast.Application.Requests.FestivalCalendar.Validators;

// These rules protect what the ML feature layer reads:
//   * FestivalKey — required, <= 50 chars, UPPERCASE [A-Z0-9_]. The Python feature layer string-matches
//     the key case-sensitively, so a lowercase key would silently miss its per-festival column. The set
//     is still open: any uppercase key is accepted, so a new festival is a new row, not a code change.
//   * Date — required, kept date-only by the mapper (it is the ML as-of-join key).
//   * LeadUpDays — >= 0, not > 0: zero is the paired-day convention for a multi-day festival. The upper
//     bound of 90 is a typo guard only; the ML imposes no cap.
//   * IsProvisional — passed straight through, never silently upgraded.
//   * Source — required on create (stricter than PolicyFlag), because festivals are curated data.
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
