using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgriForecast.Infrastructure.Database;

namespace AgriForecast.Infrastructure.Dependency_Injection;

public static class InfsDependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.DatabaseService(configuration);
        return services;
    }
}