using AgriForecast.Application.Dependency_Injection;
using AgriForecast.Infrastructure.Dependency_Injection;
using AgriForecast.Ingestion;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();

//builder.Services.AddApplicationLayer();
var host = builder.Build();
host.Run();