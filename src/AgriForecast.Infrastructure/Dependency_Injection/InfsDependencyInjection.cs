using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using AgriForecast.Infrastructure.Repositories;
using AgriForecast.Infrastructure.Services.MarketPriceIngestion;

namespace AgriForecast.Infrastructure.Dependency_Injection;

public static class InfsDependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.DatabaseService(configuration);
        
        // Repositories
        services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        services.AddScoped<IUnitofWorkRepository, UnitOfWorkRepository>();
        services.AddScoped<IDefaultSettingRepository, DefaultSettingRepository>();
        services.AddScoped<ICropRepository, CropRepository>();
        services.AddScoped<IEconimicCenterRepository, EconimicCenterRepository>();
        services.AddScoped<IMarketPriceRepository, MarketPriceRepository>();
        services.AddScoped<IMarketPriceIngestionService, MarketPriceIngestionService>();
        services.AddHttpClient<IDambullaApiClient, DambullaApiClient>(http =>
        {
            var baseUrl = configuration["MarketPriceSources:DambullaDec:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MarketPriceSources:DambullaDec:BaseUrl");

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        return services;
    }
}