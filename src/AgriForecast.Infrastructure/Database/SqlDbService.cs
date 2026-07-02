using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgriForecast.Infrastructure.Database;

public static class SqlDbService
{
    public static IServiceCollection DatabaseService(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Missing ConnectionStrings:DefaultConnection. Set it for local dev via " +
                "'dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"<value>\"', " +
                "or in production via the ConnectionStrings__DefaultConnection environment variable.");

        services.AddDbContext<AgriForecastDbContext>(options => options.UseSqlServer(connectionString));
        return services;
    }
}