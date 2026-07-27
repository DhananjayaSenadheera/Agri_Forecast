using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using AgriForecast.Infrastructure.Repositories;
using AgriForecast.Application.Services;
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
        // The Ingestion worker wires only the Infrastructure layer, so CodeSettings is registered here too
        // (the API registers it as well; a harmless duplicate).
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
        // Read-only store over the Python-owned NewsArticles capture table (raw SQL, outside the EF model).
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
        // Nightly pipeline email sentinel: behaviour knobs ("Sentinel" section) and the SMTP send seam
        // ("Smtp" section). Both singletons — immutable config and a stateless sender. Registered here so
        // the Application/API layers never see IConfiguration; the sentinel loop itself is wired in the
        // API (the Ingestion Worker resolves neither).
        services.AddSingleton<ISentinelSettings, AgriForecast.Infrastructure.Services.PipelineSentinel.SentinelSettings>();
        services.AddSingleton<ISentinelMailer, AgriForecast.Infrastructure.Services.PipelineSentinel.SmtpSentinelMailer>();
        // The clock, injectable so the pipeline-health window math is testable at a fixed instant. The
        // system provider is stateless, hence singleton.
        services.TryAddSingleton(TimeProvider.System);

        // Ingestion service control (admin start/stop) — shared by the API and the Ingestion Worker.
        // All three are SINGLETON on purpose:
        //  * the pass runner self-scopes and must outlive the request that started it;
        //  * the hosted-pass registry is the process's memory of "what can I stop" and would be amnesiac
        //    per request;
        //  * the launcher is stateless but is captured by work that outlives its scope.
        // The lock is singleton too — it holds no lease itself, it hands one out per acquisition.
        services.AddSingleton<IIngestionPassRunner, AgriForecast.Infrastructure.Services.IngestionControl.IngestionPassRunner>();
        services.AddSingleton<IIngestionPassLock, AgriForecast.Infrastructure.Services.IngestionControl.SqlIngestionPassLock>();
        services.AddSingleton<IApiHostedIngestionPasses, AgriForecast.Infrastructure.Services.IngestionControl.ApiHostedIngestionPasses>();
        services.AddSingleton<IBackgroundWorkLauncher, AgriForecast.Infrastructure.Services.IngestionControl.BackgroundWorkLauncher>();

        // Per-source ingestion watermark store. (The HARTI and CBSL typed HttpClients are registered further
        // down with the other typed clients.)
        services.AddScoped<IIngestionWatermarkRepository, IngestionWatermarkRepository>();
        // Ingestion run-tracking store: one IngestionRun row per source per pass (audit foundation).
        services.AddScoped<IIngestionRunRepository, IngestionRunRepository>();
        // The CBSL price-report and CBSL macro ingestion services are registered as typed HttpClients further
        // down, alongside the other ML-service clients.

        // Auth: user store, password hashing, and JWT issuance.
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        // Refresh-token revocation store. Scoped so it shares the request DbContext with the user repo and an
        // admin delete plus revoke commit together.
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        // Fire-safe user-activity audit: isolates every write in its own scope and swallows-and-logs, so it
        // depends only on IServiceScopeFactory.
        services.AddScoped<IUserActivityAudit, UserActivityAudit>();
        // Fire-safe system-error writer. SINGLETON, unlike the Scoped UserActivityAudit, so the storm-guard
        // window and retention counter are process-wide. Safe because it self-scopes every DB access and
        // captures no scoped dependency, and it lets the middleware constructor-inject it.
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
        // SSRF hardening: disable auto-redirect so a 3xx from the DEC portal cannot bounce the request to an
        // internal host. A redirect surfaces as a non-2xx and the client returns null.
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
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
        // News ingestion over the same ML service (POST /admin/ingest-news). The pipeline fetches RSS and
        // scores sentiment, so it needs a much longer timeout than /predict
        // (MlService:NewsIngestTimeoutSeconds, default 600).
        services.AddHttpClient<INewsIngestionService, NewsIngestionService>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            var timeoutSeconds = configuration.GetValue<int?>("MlService:NewsIngestTimeoutSeconds") ?? 600;

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        // HARTI bulletin ingestion over the same ML service (POST /admin/ingest-harti). The pipeline can
        // reparse a large PDF corpus, so the timeout is long (MlService:HartiIngestTimeoutSeconds, default
        // 1800 for a full backfill; the daily incremental pass is far shorter).
        services.AddHttpClient<IHartiBulletinIngestionService, HartiBulletinIngestionService>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            var timeoutSeconds = configuration.GetValue<int?>("MlService:HartiIngestTimeoutSeconds") ?? 1800;

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        // CBSL Daily Price Report ingestion over the same ML service (POST /admin/ingest-cbsl). The daily pass
        // parses at most a handful of 2-page PDFs, so a modest timeout suffices
        // (MlService:CbslIngestTimeoutSeconds).
        services.AddHttpClient<ICbslPriceReportIngestionService, CbslPriceReportIngestionService>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            var timeoutSeconds = configuration.GetValue<int?>("MlService:CbslIngestTimeoutSeconds") ?? 300;

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        // CBSL macro ingestion over the same ML service (POST /admin/ingest-cbsl-macro). The pipeline parses a
        // small monthly PDF corpus, so 60s is comfortable (MlService:CbslMacroIngestTimeoutSeconds).
        services.AddHttpClient<ICbslMacroIngestionService, CbslMacroIngestionService>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            var timeoutSeconds = configuration.GetValue<int?>("MlService:CbslMacroIngestTimeoutSeconds") ?? 60;

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        })
        // SSRF hardening, as on the Dambulla client: disable auto-redirect so a 3xx from the ML host cannot
        // bounce an authenticated admin POST to an internal host.
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
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
