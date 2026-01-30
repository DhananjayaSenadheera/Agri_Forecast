using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgriForecast.Infrastructure.Database;

public static class SqlDbService
{
    public static IServiceCollection DatabaseService(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        services.AddDbContext<AgriForecastDbContext>(options => options.UseSqlServer(connectionString));
        return services;
    }
}