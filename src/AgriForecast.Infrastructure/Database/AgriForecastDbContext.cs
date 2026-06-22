using AgriForecast.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Database;

public class AgriForecastDbContext(DbContextOptions<AgriForecastDbContext> options) : DbContext(options) 
{
    public DbSet<Crop> Crops { get; set; }
    public DbSet<EconomicCenter> EconomicCenters { get; set; }
    public DbSet<DefaultSetting> DefaultSettings { get; set; }
    public DbSet<MarketPrice> MarketPrices { get; set; }
    public DbSet<CropPrice> CropPrices { get; set; }
    public DbSet<WeatherRecord> WeatherRecords { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DefaultSetting>().HasData(new DefaultSetting
        {
            Id = 1,
            Crop_Code = 1,
            Crop_Padding = 8,
            Crop_Prefix = "CROP",
            Eco_Code = 1,
            Eco_Padding = 8,
            Eco_Prefix = "ECO",
        });
        
        modelBuilder.Entity<WeatherRecord>(e =>
        {
            e.Property(x => x.AvgTemperature).HasPrecision(6, 2);
            e.Property(x => x.TotalRainfall).HasPrecision(8, 2);
        });

        modelBuilder.Entity<CropPrice>(e =>
        {
            e.Property(x => x.AveragePrice).HasPrecision(18, 2);
            e.HasIndex(x => new { x.CropId, x.EconomicCenterId, x.Month }).IsUnique();
        });

        modelBuilder.Entity<MarketPrice>(e =>
        {
            e.Property(x => x.MinPrice).HasPrecision(18, 2);
            e.Property(x => x.MaxPrice).HasPrecision(18, 2);

            // prevents duplicates even if worker runs twice
            e.HasIndex(x => new { x.Source, x.ExternalProductId, x.PriceDate })
                .IsUnique();
        });

    }
}
