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
        // R2 D-DF4: the ingestion auto-provision path now stamps category-prefixed CropCodes via
        // CodeSettings (same counter as the manual CQRS path). The Ingestion worker wires only the
        // Infrastructure layer (not AddApplicationLayer), so register CodeSettings here too — its
        // only dependency, IDefaultSettingRepository, is registered above. Matches the Transient
        // lifetime used in ApplicationDependencyInjection (the API registers both; harmless dup).
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
        services.AddScoped<IForecastingService, ForecastingService>();
        services.AddScoped<IRecommendationService, RecommendationService>();
        // Read-only market-overview snapshot store (GET /api/forecast/market-overview).
        services.AddScoped<IMarketOverviewStore, AgriForecast.Infrastructure.Services.MarketOverview.MarketOverviewStore>();
        // API-1: read-only market registry store (GET /api/markets/get/all).
        services.AddScoped<IMarketReadStore, AgriForecast.Infrastructure.Services.MarketRead.MarketReadStore>();
        // API-2: read-only price-history store (GET /api/prices/crop/{cropId}/history).
        services.AddScoped<IPriceHistoryStore, AgriForecast.Infrastructure.Services.PriceHistory.PriceHistoryStore>();
        // API-11: read-only economic-indicator + macro-series store (GET /api/indicators,
        // /api/macro-series, /api/indicators/catalog) for the admin Indicators page (ADM-6).
        services.AddScoped<IIndicatorReadStore, AgriForecast.Infrastructure.Services.IndicatorRead.IndicatorReadStore>();
        // PR 3: read-only ingestion-audit store (GET /api/admin/ingestion/status + /runs) for the
        // admin ingestion page. Reads only — uses the normal request-scoped DbContext (unlike the
        // write-side IngestionRunRepository, which isolates every write in its own scope).
        services.AddScoped<IIngestionReadStore, AgriForecast.Infrastructure.Services.IngestionRead.IngestionReadStore>();
        // Logs hub PR A: read-only Logs-hub store (GET /api/admin/logs/training + /user-activity) for
        // the admin Logs page. Reads only — normal request-scoped DbContext (mirrors IngestionReadStore).
        services.AddScoped<ILogsReadStore, AgriForecast.Infrastructure.Services.LogsRead.LogsReadStore>();
        // PR 3: resolves the status handler's config values (Ingestion:ServiceAddress /
        // RunningStalenessMinutes) at the Infrastructure boundary, keeping config out of Application.
        services.AddScoped<IIngestionStatusSettings, AgriForecast.Infrastructure.Services.IngestionRead.IngestionStatusSettings>();

        // R1.1 P1 Step 6: per-source ingestion watermark store + the CBSL ingestion service.
        // (The HARTI + CBSL typed HttpClients are registered further down alongside the other
        // typed clients.)
        services.AddScoped<IIngestionWatermarkRepository, IngestionWatermarkRepository>();
        // Ingestion run-tracking store: one IngestionRun row per source per pass (audit foundation).
        services.AddScoped<IIngestionRunRepository, IngestionRunRepository>();
        services.AddScoped<ICbslPriceReportIngestionService, CbslPriceReportIngestionService>();
        // R1 P3 (86cahefbh): CBSL macro (CCPI/MEI vintage) ingestion service — thin, feature-flagged
        // OFF skeleton over the Python /admin/ingest-cbsl-macro seam. Its typed HttpClient is
        // registered further down alongside the other ML-service clients.

        // Auth: user store, password hashing, and JWT issuance.
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
        // Refresh-token revocation store (persisted jti / token-family). Scoped: shares the
        // request-scoped DbContext with the user repo so an admin delete + revoke commit together.
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        // Logs hub PR A: fire-safe user-activity audit. Isolates every write in its own scope and
        // swallows-and-logs (never adds a failure mode to login/registration/admin ops) — same B1
        // discipline as IngestionRunRepository, so it depends only on IServiceScopeFactory.
        services.AddScoped<IUserActivityAudit, UserActivityAudit>();
        // Logs hub PR A (Phase 3): fire-safe system-error writer for GlobalExceptionMiddleware's 500
        // path. SINGLETON (deliberate deviation from UserActivityAudit's Scoped) so the storm-guard
        // window + retention counter are process-wide instance state; safe because it self-scopes every
        // DB access and captures no scoped dependency, and it lets the middleware constructor-inject it.
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
        // SSRF hardening (S1): the DEC portal probes fixed JSON endpoints. Disable
        // auto-redirect so a 3xx from the portal cannot silently bounce the request
        // to an internal/arbitrary host; a redirect surfaces as a non-2xx and the
        // client returns null (handled as a failed fetch), never followed blindly.
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
        // News ingestion: typed HttpClient over the same ML service, triggering
        // the Python news pipeline via POST /admin/ingest-news (orchestrated by
        // the Ingestion Worker). The pipeline fetches RSS + scores sentiment, so
        // it needs a much longer timeout than /predict (override via
        // MlService:NewsIngestTimeoutSeconds, default 600).
        services.AddHttpClient<INewsIngestionService, NewsIngestionService>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            var timeoutSeconds = configuration.GetValue<int?>("MlService:NewsIngestTimeoutSeconds") ?? 600;

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        // HARTI multi-market bulletin ingestion: typed HttpClient over the same ML service,
        // triggering the Python HARTI pipeline via POST /admin/ingest-harti (orchestrated by the
        // Ingestion Worker). The pipeline can (re)parse a large PDF corpus + run data-quality
        // hooks, so it needs a long timeout — much longer than /predict (override via
        // MlService:HartiIngestTimeoutSeconds, default 1800 = 30 min for a full backfill; the
        // daily incremental pass is far shorter).
        services.AddHttpClient<IHartiBulletinIngestionService, HartiBulletinIngestionService>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            var timeoutSeconds = configuration.GetValue<int?>("MlService:HartiIngestTimeoutSeconds") ?? 1800;

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        // CBSL daily price report: typed HttpClient skeleton. The service is feature-flagged OFF
        // (Disabled watermark) until a Python CBSL parser exists, so no CBSL BaseUrl is required
        // yet; the client is registered so the seam is real and DI-resolvable. BaseAddress is set
        // from MarketPriceSources:Cbsl:BaseUrl when present (optional today).
        services.AddHttpClient<ICbslPriceReportClient, CbslPriceReportClient>(http =>
        {
            var baseUrl = configuration["MarketPriceSources:Cbsl:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
                http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(60);
        });
        // R1 P3 (86cahefbh): CBSL macro ingestion — typed HttpClient over the same ML service,
        // triggering the Python macro pipeline via POST /admin/ingest-cbsl-macro (orchestrated by
        // the Ingestion Worker). BaseAddress = MlService:BaseUrl (the /admin/* seam lives on the ML
        // service, same as HARTI/news). The pipeline scrapes + parses a small monthly PDF corpus, so
        // a 60s timeout is comfortable (override via MlService:CbslMacroIngestTimeoutSeconds).
        services.AddHttpClient<ICbslMacroIngestionService, CbslMacroIngestionService>(http =>
        {
            var baseUrl = configuration["MlService:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Missing MlService:BaseUrl");

            var timeoutSeconds = configuration.GetValue<int?>("MlService:CbslMacroIngestTimeoutSeconds") ?? 60;

            http.BaseAddress = new Uri(baseUrl);
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        })
        // SSRF hardening (matches the Dambulla client): disable auto-redirect so a 3xx from the ML
        // host cannot silently bounce an authenticated admin POST to an internal/arbitrary host — a
        // redirect surfaces as a non-2xx and is handled as a failed pass, never followed blindly.
        // (The recorded lesson notes the existing CBSL price-report client block lacks this.)
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
