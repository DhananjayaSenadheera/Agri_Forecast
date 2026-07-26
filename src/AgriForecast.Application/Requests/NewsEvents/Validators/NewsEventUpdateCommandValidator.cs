using AgriForecast.Application.Requests.NewsEvents.Commands.Update;
using AgriForecast.Domain.Interfaces;
using FluentValidation;

namespace AgriForecast.Application.Requests.NewsEvents.Validators;

// Mirrors NewsEventCreateCommandValidator plus the Id rule. There is no PublishedAt rule because the
// UpdateDto does not carry it — immutability is enforced by omission on the wire.
public class NewsEventUpdateCommandValidator : AbstractValidator<NewsEventUpdateCommand>
{
    private readonly INewsEventRepository _newsEventRepository;

    public NewsEventUpdateCommandValidator(INewsEventRepository newsEventRepository)
    {
        _newsEventRepository = newsEventRepository;

        RuleFor(x => x.NewsEventUpdateDto).NotNull().WithMessage("News event details are required.");

        RuleFor(x => x.NewsEventUpdateDto.Id)
            .NotEmpty().WithMessage("Id is required.");

        RuleFor(x => x.NewsEventUpdateDto.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(300).WithMessage("Title cannot exceed 300 characters.");

        RuleFor(x => x.NewsEventUpdateDto.Description)
            .MaximumLength(4000).WithMessage("Description cannot exceed 4000 characters.");

        RuleFor(x => x.NewsEventUpdateDto.EventType)
            .IsInEnum().WithMessage("EventType must be a defined event type (0..8).");

        RuleFor(x => x.NewsEventUpdateDto.Direction)
            .IsInEnum().WithMessage("Direction must be -1, 0, or 1.");

        RuleFor(x => x.NewsEventUpdateDto.SourceUrl)
            .Must(NewsEventUrl.BeValidAbsoluteHttpUrl)
            .When(x => x.NewsEventUpdateDto is not null && !string.IsNullOrWhiteSpace(x.NewsEventUpdateDto.SourceUrl))
            .WithMessage("SourceUrl must be a valid absolute http(s) URL.");

        RuleFor(x => x.NewsEventUpdateDto.AffectedCropIds)
            .MustAsync((ids, ct) => _newsEventRepository.CropsExistAsync(ids))
            .WithMessage("One or more affected crop ids do not exist.");

        RuleFor(x => x.NewsEventUpdateDto.AffectedMarketIds)
            .MustAsync((ids, ct) => _newsEventRepository.MarketsExistAsync(ids))
            .WithMessage("One or more affected market ids do not exist.");
    }
}
