using AgriForecast.Application.Helper;
using AgriForecast.Application.Mapper;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace AgriForecast.Application.Dependency_Injection;

public static class ApplicationDependencyInjection
{
    public static IServiceCollection AddApplicationLayer(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ApplicationDependencyInjection).Assembly));
        services.AddAutoMapper(typeof(ProfileMapper));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        return services;
    }
}