using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using AgriForecast.Infrastructure.Repositories;
using AgriForecast.Application.Services;
using AgriForecast.Infrastructure.Services.Forecasting;
using AgriForecast.Infrastructure.Services.MarketPriceIngestion;
using AgriForecast.Infrastructure.Services.Recommendation;

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
        services.AddScoped<ICropPriceRepository, CropPriceRepository>();
        services.AddScoped<IWeatherRecordRepository, WeatherRecordRepository>();
        services.AddScoped<IForecastingService, ForecastingService>();
        services.AddScoped<IRecommendationService, RecommendationService>();
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