using AgriForecast.Application.common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace AgriForecast.Application.Dependency_Injection;

public static class ApplicationDependencyInjection
{
    public static IServiceCollection AddApplicationLayer(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ApplicationDependencyInjection).Assembly));
        // Mapping is now hand-written static mapper classes (Mapper/*Mapper.cs) — no DI registration needed.
        services.AddValidatorsFromAssembly(typeof(ApplicationDependencyInjection).Assembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient<CodeSettings>();
        return services;
    }
}