using AgriForecast.Application.Requests.Crop.Commands.Create;
using AgriForecast.Domain.Interfaces;
using FluentValidation;

namespace AgriForecast.Application.Requests.Crop.Validators;

public class CropCreateValidator : AbstractValidator<CropCreateCommand>
{
    private readonly ICropCategoryRepository _cropCategoryRepository;

    public CropCreateValidator(ICropCategoryRepository cropCategoryRepository)
    {
        _cropCategoryRepository = cropCategoryRepository;

        RuleFor(c => c.CreateDto.Name)
            .NotEmpty().WithMessage("Crop name is required.")
            .MaximumLength(100).WithMessage("Crop name must not exceed 100 characters.");

        // A manual crop must be filed under an existing CropCategory. NotEmpty rejects the
        // default/empty Guid; MustAsync verifies the category actually exists (the FK is
        // nullable at the DB level, so this validator is what enforces the "required" rule).
        RuleFor(c => c.CreateDto.CropCategoryId)
            .NotEmpty().WithMessage("Crop category is required.")
            .MustAsync((id, ct) => _cropCategoryRepository.ExistsAsync(id, ct))
            .WithMessage("Crop category does not exist.");
    }
}
