using AgriForecast.Application.Dependency_Injection;
using AgriForecast.Infrastructure.Dependency_Injection;
using AgriForecast.Ingestion;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
    // Local dev secrets (e.g. ConnectionStrings:DefaultConnection) come from user-secrets;
    // prod overrides via ConnectionStrings__DefaultConnection environment variable.
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

// Same boot guard the API applies: this host runs ingestion passes, so a missing structural setting is a
// loud startup failure, not a silent screenful of failed sources hours later.
AgriForecast.Infrastructure.Services.IngestionControl.IngestionPassConfiguration
    .ThrowIfIncomplete(builder.Configuration);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();