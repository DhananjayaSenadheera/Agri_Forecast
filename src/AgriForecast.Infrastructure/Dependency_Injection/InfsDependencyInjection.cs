using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using AgriForecast.Infrastructure.Repositories;
using AgriForecast.Application.Services;
using AgriForecast.Infrastructure.ExternalSources.Clients;
using AgriForecast.Infrastructure.Services.Forecasting;
using AgriForecast.Infrastructure.Services.MarketPriceIngestion;
using AgriForecast.Infrastructure.Services.WeatherIngestion;
using AgriForecast.Infrastructure.Services.EconomicIngestion;
using AgriForecast.Infrastructure.Services.NewsIngestion;
using AgriForecast.Infrastructure.Services.HartiIngestion;
using AgriForecast.Infrastructure.Services.CbslIngestion;
using AgriForecast.Infrastructure.Services.CbslMacroIngestion;
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
        services.AddScoped<ICropCategoryRepository, CropCategoryRepository>();
        services.AddScoped<IMarketPriceRepository, MarketPriceRepository>();
        services.AddTransient<AgriForecast.Application.common.CodeSettings>();
        services.AddScoped<IMarketPriceIngestionService, MarketPriceIngestionService>();
        services.AddScoped<IWeatherIngestionService, WeatherIngestionService>();
        services.AddScoped<IEconomicIngestionService, EconomicIngestionService>();
        services.AddScoped<ICropPriceRepository, CropPriceRepository>();
        services.AddScoped<IWeatherRecordRepository, WeatherRecordRepository>();
        services.AddScoped<IEconomicIndicatorRepository, EconomicIndicatorRepository>();
        services.AddScoped<IMacroSeriesPointRepository, MacroSeriesPointRepository>();
        services.AddScoped<IPolicyFlagRepository, PolicyFlagRepository>();
        services.AddScoped<IFestivalCalendarRepository, FestivalCalendarRepository>();
        services.AddScoped<INewsEventRepository, NewsEventRepository>();
        services.AddScoped<Application.Services.INewsArticleReadStore, Services.NewsArticleRead.NewsArticleReadStore>();
        services.AddScoped<IForecastingService, ForecastingService>();
        services.AddScoped<IRecommendationService, RecommendationService>();
        // Read-only market-overview snapshot store (GET /api/forecast/market-overview).
        services.AddScoped<IMarketOverviewStore, AgriForecast.Infrastructure.Services.MarketOverview.MarketOverviewStore>();
        // API-1: read-only market registry store (GET /api/markets/get/all).
        services.AddScoped<IMarketReadStore, AgriForecast.Infrastructure.Services.MarketRead.MarketReadStore>();
        // API-2: read-only price-history store (GET /api/prices/crop/{cropId}/history).
        services.AddScoped<IPriceHistoryStore, AgriForecast.Infrastructure.Services.PriceHistory.PriceHistoryStore>();
        // Read-only economic-indicator and macro-series store for the admin Indicators page.
        services.AddScoped<IIndicatorReadStore, AgriForecast.Infrastructure.Services.IndicatorRead.IndicatorReadStore>();
        // Read-only ingestion-audit store. Reads only, so it uses the normal request-scoped DbContext, unlike
        // the write-side IngestionRunRepository which isolates every write in its own scope.
        services.AddScoped<IIngestionReadStore, AgriForecast.Infrastructure.Services.IngestionRead.IngestionReadStore>();
        // Read-only Logs-hub store. Reads only, on the normal request-scoped DbContext.
        services.AddScoped<ILogsReadStore, AgriForecast.Infrastructure.Services.LogsRead.LogsReadStore>();
        // Read-only ForecastSnapshots store behind the admin "Forecast accuracy" surface. Reads only —
        // the nightly Python job owns every write to that table.
        services.AddScoped<IForecastAccuracyReadStore, AgriForecast.Infrastructure.Services.ForecastAccuracyRead.ForecastAccuracyReadStore>();
        // Read-only store behind the farmer portfolio (GET /api/portfolio/watchlist and /dashboard). Every
        // read it exposes is owner-scoped by signature; the watchlist WRITES go through the repository below.
        services.AddScoped<IPortfolioReadStore, AgriForecast.Infrastructure.Services.PortfolioRead.PortfolioReadStore>();
        // Resolves the status handler's config at the Infrastructure boundary, keeping configuration out of
        // the Application layer.
        services.AddScoped<IIngestionStatusSettings, AgriForecast.Infrastructure.Services.IngestionRead.IngestionStatusSettings>();
        // Window-scoped read store + schedule config behind GET /api/admin/pipeline/health ("did last
        // night's pipeline run?"). The schedule mirrors k8s/pipeline-daily.yaml.
        services.AddScoped<IPipelineHealthReadStore, AgriForecast.Infrastructure.Services.PipelineHealth.PipelineHealthReadStore>();
        // SINGLETON, unlike the read store next to it: this is immutable configuration with no DbContext
        // behind it, and the API's nightly sentinel is a singleton hosted service that needs the same
        // schedule the request-scoped handler uses. A scoped registration would be a captive dependency.
        services.AddSingleton<IPipelineScheduleSettings, AgriForecast.Infrastructure.Services.PipelineHealth.PipelineScheduleSettings>();
        // Same reasoning as the line above — immutable config read by BOTH the request-scoped health
        // handler and the singleton sentinel, so it must be a singleton to avoid a captive dependency.
        // Describes the MONTHLY macro CronJob, not the nightly one.
        services.AddSingleton<IMacroFreshnessSettings, AgriForecast.Infrastructure.Services.PipelineHealth.MacroFreshnessSettings>();
        services.AddSingleton<ISentinelSettings, AgriForecast.Infrastructure.Services.PipelineSentinel.SentinelSettings>();
        services.AddSingleton<ISentinelMailer, AgriForecast.Infrastructure.Services.PipelineSentinel.SmtpSentinelMailer>();
        services.TryAddSingleton(TimeProvider.System);
        
        services.AddSingleton<IIngestionPassRunner, AgriForecast.Infrastructure.Services.IngestionControl.IngestionPassRunner>();
        services.AddSingleton<IIngestionPassLock, AgriForecast.Infrastructure.Services.IngestionControl.SqlIngestionPassLock>();
        services.AddSingleton<IApiHostedIngestionPasses, AgriForecast.Infrastructure.Services.IngestionControl.ApiHostedIngestionPasses>();
        services.AddSingleton<IBackgroundWorkLauncher, AgriForecast.Infrastructure.Services.IngestionControl.BackgroundWorkLauncher>();
        services.AddScoped<IIngestionWatermarkRepository, IngestionWatermarkRepository>();
        services.AddScoped<IIngestionRunRepository, IngestionRunRepository>();
        services.AddScoped<IUserCropWatchlistRepository, UserCropWatchlistRepository>();
        // Scoped, not singleton: it shares the request's DbContext so its insert is committed by the
        // handler's unit of work, in the same transaction as the planting date it explains.
        services.AddScoped<IPlantedDateRemovalRepository, PlantedDateRemovalRepository>();
        // Scoped for the same reason: the sales handlers mutate what they load and commit through the
        // request's unit of work, so the repository must share that DbContext.
        services.AddScoped<IUserSaleRepository, UserSaleRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IUserActivityAudit, UserActivityAudit>();
        services.AddSingleton<ISystemErrorLog, SystemErrorLog>();
        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
        services.AddHttpClient<IDambullaApiClient, DambullaApiClient>(http =>
        {
            var baseUrl = configuration["MarketPriceSources:DambullaDec:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MarketPriceSources:DambullaDec:BaseUrl");

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
        });
        services.AddHttpClient<IHarvestPredictionClient, HarvestPredictionClient>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<INewsIngestionService, NewsIngestionService>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            var timeoutSeconds = configuration.GetValue<int?>("MlService:NewsIngestTimeoutSeconds") ?? 600;

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        services.AddHttpClient<IHartiBulletinIngestionService, HartiBulletinIngestionService>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            var timeoutSeconds = configuration.GetValue<int?>("MlService:HartiIngestTimeoutSeconds") ?? 1800;

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        services.AddHttpClient<ICbslPriceReportIngestionService, CbslPriceReportIngestionService>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            var timeoutSeconds = configuration.GetValue<int?>("MlService:CbslIngestTimeoutSeconds") ?? 300;

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        services.AddHttpClient<ICbslMacroIngestionService, CbslMacroIngestionService>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            var timeoutSeconds = configuration.GetValue<int?>("MlService:CbslMacroIngestTimeoutSeconds") ?? 60;

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
        });
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
        
        services.AddHttpClient<IEconomicDataClient, OpenErApiClient>(http =>
        {
            http.BaseAddress = new Uri(configuration["EconomicSource:OpenErApi:BaseUrl"] ?? "https://open.er-api.com/");
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<IFxHistoricalClient, FawazCurrencyFxClient>(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(20);
        });

        return services;
    }
}
