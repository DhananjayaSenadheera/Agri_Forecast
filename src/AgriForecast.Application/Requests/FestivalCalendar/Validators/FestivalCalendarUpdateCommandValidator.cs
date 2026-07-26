using AgriForecast.Application.Requests.FestivalCalendar.Commands.Update;
using FluentValidation;

namespace AgriForecast.Application.Requests.FestivalCalendar.Validators;

// Mirrors FestivalCalendarCreateCommandValidator plus the Id rule; see that file for the per-field
// reasoning. Source is required on every edit.
public class FestivalCalendarUpdateCommandValidator : AbstractValidator<FestivalCalendarUpdateCommand>
{
    public FestivalCalendarUpdateCommandValidator()
    {
        RuleFor(x => x.FestivalCalendarUpdateDto).NotNull().WithMessage("Festival details are required.");

        RuleFor(x => x.FestivalCalendarUpdateDto.Id)
            .NotEmpty().WithMessage("Id is required.");

        RuleFor(x => x.FestivalCalendarUpdateDto.FestivalKey)
            .NotEmpty().WithMessage("FestivalKey is required.")
            .MaximumLength(50).WithMessage("FestivalKey cannot exceed 50 characters.")
            .Matches("^[A-Z0-9_]+$").WithMessage(
                "FestivalKey must be uppercase letters, digits or underscore (e.g. AVURUDU, THAI_PONGAL).");

        RuleFor(x => x.FestivalCalendarUpdateDto.Date)
            .NotEmpty().WithMessage("Date is required.");

        RuleFor(x => x.FestivalCalendarUpdateDto.LeadUpDays)
            .GreaterThanOrEqualTo(0).WithMessage("LeadUpDays cannot be negative.")
            .LessThanOrEqualTo(90).WithMessage("LeadUpDays cannot exceed 90.");

        RuleFor(x => x.FestivalCalendarUpdateDto.Source)
            .NotEmpty().WithMessage("Source is required when editing a festival.")
            .MaximumLength(300).WithMessage("Source cannot exceed 300 characters.");
    }
}
