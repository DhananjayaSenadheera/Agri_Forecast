using AgriForecast.Application.Requests.NewsEvents.Commands.Create;
using AgriForecast.Domain.Interfaces;
using FluentValidation;

namespace AgriForecast.Application.Requests.NewsEvents.Validators;

// Enum-int values are validated defined-only, and crop/market link ids must resolve to real rows so a bad
// id is a structured 400 rather than a raw FK error. PublishedAt is required on create.
public class NewsEventCreateCommandValidator : AbstractValidator<NewsEventCreateCommand>
{
    private readonly INewsEventRepository _newsEventRepository;

    public NewsEventCreateCommandValidator(INewsEventRepository newsEventRepository)
    {
        _newsEventRepository = newsEventRepository;

        RuleFor(x => x.NewsEventCreateDto).NotNull().WithMessage("News event details are required.");

        RuleFor(x => x.NewsEventCreateDto.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(300).WithMessage("Title cannot exceed 300 characters.");

        RuleFor(x => x.NewsEventCreateDto.Description)
            .MaximumLength(4000).WithMessage("Description cannot exceed 4000 characters.");

        // Defined-enum check (0..8). IsInEnum rejects an out-of-range int.
        RuleFor(x => x.NewsEventCreateDto.EventType)
            .IsInEnum().WithMessage("EventType must be a defined event type (0..8).");

        // Direction whitelist {-1, 0, 1} — PolicyDirection is exactly that set, so IsInEnum enforces it.
        RuleFor(x => x.NewsEventCreateDto.Direction)
            .IsInEnum().WithMessage("Direction must be -1, 0, or 1.");

        // PublishedAt = knowledge/as-of/vintage date. Required on create; immutable afterwards.
        RuleFor(x => x.NewsEventCreateDto.PublishedAt)
            .NotEmpty().WithMessage("PublishedAt (knowledge date) is required.");

        // SourceUrl optional, but if present must be a well-formed absolute http(s) URL.
        RuleFor(x => x.NewsEventCreateDto.SourceUrl)
            .Must(NewsEventUrl.BeValidAbsoluteHttpUrl)
            .When(x => x.NewsEventCreateDto is not null && !string.IsNullOrWhiteSpace(x.NewsEventCreateDto.SourceUrl))
            .WithMessage("SourceUrl must be a valid absolute http(s) URL.");

        // Optional links — every id must resolve to an existing Crop / Market.
        RuleFor(x => x.NewsEventCreateDto.AffectedCropIds)
            .MustAsync((ids, ct) => _newsEventRepository.CropsExistAsync(ids))
            .WithMessage("One or more affected crop ids do not exist.");

        RuleFor(x => x.NewsEventCreateDto.AffectedMarketIds)
            .MustAsync((ids, ct) => _newsEventRepository.MarketsExistAsync(ids))
            .WithMessage("One or more affected market ids do not exist.");
    }
}
