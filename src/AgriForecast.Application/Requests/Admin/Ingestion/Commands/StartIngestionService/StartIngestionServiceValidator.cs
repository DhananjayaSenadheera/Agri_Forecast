using FluentValidation;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Commands.StartIngestionService;

// The command has no client-supplied fields, so the only thing worth asserting is that the controller
// actually stamped the acting admin. In practice the controller 401s first when the JWT subject is
// missing; this is the belt that stops a future call site from queueing an unattributable pass.
public class StartIngestionServiceValidator : AbstractValidator<StartIngestionServiceCommand>
{
    public StartIngestionServiceValidator()
    {
        RuleFor(x => x.ActingUserId)
            .NotEmpty()
            .WithMessage("The acting user could not be identified.");
    }
}
