using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using AgriForecast.Infrastructure.Repositories;
using AgriForecast.Application.Services;
using AgriForecast.Infrastructure.Services.Forecasting;
using AgriForecast.Infrastructure.Services.MarketPriceIngestion;
using AgriForecast.Infrastructure.Services.WeatherIngestion;
using AgriForecast.Infrastructure.Services.EconomicIngestion;
using AgriForecast.Infrastructure.Services.Recommendation;
using AgriForecast.Infrastructure.Security;

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
        services.AddScoped<IWeatherIngestionService, WeatherIngestionService>();
        services.AddScoped<IEconomicIngestionService, EconomicIngestionService>();
        services.AddScoped<ICropPriceRepository, CropPriceRepository>();
        services.AddScoped<IWeatherRecordRepository, WeatherRecordRepository>();
        services.AddScoped<IEconomicIndicatorRepository, EconomicIndicatorRepository>();
        services.AddScoped<IPolicyFlagRepository, PolicyFlagRepository>();
        services.AddScoped<IForecastingService, ForecastingService>();
        services.AddScoped<IRecommendationService, RecommendationService>();

        // Auth: user store, password hashing, and JWT issuance.
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
        services.AddHttpClient<IDambullaApiClient, DambullaApiClient>(http =>
        {
            var baseUrl = configuration["MarketPriceSources:DambullaDec:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MarketPriceSources:DambullaDec:BaseUrl");

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        // Typed HttpClient over the Python ML service (POST /predict).
        services.AddHttpClient<IHarvestPredictionClient, HarvestPredictionClient>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        // Weather provider is swappable via WeatherSource:Provider (default: OpenMeteo - free, keyless).
        var weatherProvider = configuration["WeatherSource:Provider"] ?? "OpenMeteo";
        if (weatherProvider.Equals("OpenWeather", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<IWeatherClient, OpenWeatherClient>(http =>
            {
                http.BaseAddress = new Uri(configuration["WeatherSource:OpenWeather:BaseUrl"] ?? "https://api.openweathermap.org/");
                http.Timeout = TimeSpan.FromSeconds(30);
            });
        }
        else
        {
            services.AddHttpClient<IWeatherClient, OpenMeteoClient>(http =>
            {
                http.BaseAddress = new Uri(configuration["WeatherSource:OpenMeteo:BaseUrl"] ?? "https://archive-api.open-meteo.com/");
                http.Timeout = TimeSpan.FromSeconds(60);
            });
        }

        // Economic data provider (USD/LKR FX) — open.er-api.com (free, keyless, latest-only).
        services.AddHttpClient<IEconomicDataClient, OpenErApiClient>(http =>
        {
            http.BaseAddress = new Uri(configuration["EconomicSource:OpenErApi:BaseUrl"] ?? "https://open.er-api.com/");
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        // Historical FX backfill — fawazahmed0/exchange-api on Cloudflare Pages (free, keyless, daily history).
        // The client builds absolute per-date URLs ({date}.currency-api.pages.dev), so no BaseAddress is set.
        services.AddHttpClient<IFxHistoricalClient, FawazCurrencyFxClient>(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(20);
        });

        return services;
    }
}
