using FluentValidation;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Commands.StopIngestionService;

// Mirrors the start validator: the only client-independent field is the JWT-stamped actor, and an
// unattributable stop must not be accepted into the audit trail.
public class StopIngestionServiceValidator : AbstractValidator<StopIngestionServiceCommand>
{
    public StopIngestionServiceValidator()
    {
        RuleFor(x => x.ActingUserId)
            .NotEmpty()
            .WithMessage("The acting user could not be identified.");
    }
}
