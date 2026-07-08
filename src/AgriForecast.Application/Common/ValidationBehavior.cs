using FluentValidation;
using MediatR;

namespace AgriForecast.Application.common;

public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var context = new ValidationContext<TRequest>(request);

        // ValidateAsync (not the synchronous Validate) so validators may use async rules
        // such as MustAsync for DB existence checks (e.g. CropCreateValidator verifying the
        // CropCategory exists). Calling Validate on a validator that has an async rule throws
        // AsyncValidatorInvokedSynchronouslyException, so async is required, not optional.
        var results = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var failures = results
            .SelectMany(result => result.Errors)
            .Where(error => error != null)
            .ToList();

        if(failures.Count !=0)
            throw new ValidationException(failures);
        return await next(cancellationToken);
    }
}