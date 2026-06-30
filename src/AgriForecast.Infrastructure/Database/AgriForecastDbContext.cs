using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
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
    public DbSet<EconomicIndicator> EconomicIndicators { get; set; }
    public DbSet<PolicyFlag> PolicyFlags { get; set; }
    public DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(50).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.Role).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

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
        
        modelBuilder.Entity<Crop>(e =>
        {
            e.Property(x => x.PlantingSeason).HasMaxLength(20);
        });

        modelBuilder.Entity<WeatherRecord>(e =>
        {
            e.Property(x => x.AvgTemperature).HasPrecision(6, 2);
            e.Property(x => x.TotalRainfall).HasPrecision(8, 2);
        });

        modelBuilder.Entity<EconomicIndicator>(e =>
        {
            e.Property(x => x.IndicatorCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.Source).HasMaxLength(100).IsRequired();
            e.Property(x => x.Value).HasPrecision(18, 6);

            // One reading per (date, indicator) — keeps ingestion idempotent at the DB level.
            e.HasIndex(x => new { x.Date, x.IndicatorCode }).IsUnique();
        });

        modelBuilder.Entity<PolicyFlag>(e =>
        {
            e.Property(x => x.PolicyType).HasConversion<int>().IsRequired();
            e.Property(x => x.Direction).HasConversion<int>().IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Source).HasMaxLength(200);
            e.Property(x => x.ReferenceUrl).HasMaxLength(500);

            // Dates stored date-only (no time) — these are the point-in-time keys
            // the ML layer as-of-joins on, so they must never carry a hidden time.
            e.Property(x => x.EffectiveFrom).HasColumnType("date").IsRequired();
            e.Property(x => x.EffectiveTo).HasColumnType("date");

            // Primary lookup pattern is "what was active as-of date D", which scans EffectiveFrom.
            e.HasIndex(x => x.EffectiveFrom);
        });

        SeedPolicyFlags(modelBuilder);

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

    // Real Sri Lankan national policies, captured point-in-time for the ML feature store.
    // Fixed Ids + a fixed CreatedAtUtc keep the seed deterministic (no "now" leakage) and
    // idempotent across migrations. Dates are date-only.
    private static void SeedPolicyFlags(ModelBuilder modelBuilder)
    {
        var seededAt = new DateTime(2026, 06, 30, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<PolicyFlag>().HasData(
            new PolicyFlag
            {
                Id = Guid.Parse("a1f1c001-0000-0000-0000-000000000001"),
                PolicyType = PolicyType.ImportBan,
                Title = "Chemical fertiliser & agrochemical import ban",
                Description = "Government banned imports of chemical fertilisers, pesticides and weedicides, forcing a nationwide shift to organic farming. Cut yields sharply across paddy and vegetables, pushing harvest-time prices up.",
                EffectiveFrom = new DateTime(2021, 05, 06),
                EffectiveTo = new DateTime(2021, 11, 24),
                Direction = PolicyDirection.Bullish,
                Source = "Government of Sri Lanka",
                ReferenceUrl = "https://en.wikipedia.org/wiki/2021%E2%80%932022_Sri_Lankan_political_crisis",
                CreatedAtUtc = seededAt
            },
            new PolicyFlag
            {
                Id = Guid.Parse("a1f1c001-0000-0000-0000-000000000002"),
                PolicyType = PolicyType.FertiliserSubsidy,
                Title = "Aswesuma / fertiliser cash subsidy for paddy farmers",
                Description = "Reinstated fertiliser support for the 2022/23 Maha season via direct cash and subsidised fertiliser to paddy farmers, easing input costs and partially recovering yields.",
                EffectiveFrom = new DateTime(2022, 10, 01),
                EffectiveTo = new DateTime(2023, 03, 31),
                Direction = PolicyDirection.Bearish,
                Source = "Ministry of Agriculture, Sri Lanka",
                ReferenceUrl = null,
                CreatedAtUtc = seededAt
            },
            new PolicyFlag
            {
                Id = Guid.Parse("a1f1c001-0000-0000-0000-000000000003"),
                PolicyType = PolicyType.FuelPriceChange,
                Title = "Monthly fuel price formula (CPC pricing formula)",
                Description = "Introduction of a transparent monthly fuel pricing formula. Transport/diesel cost feeds into farm-gate to wholesale transport margins; ongoing, still in effect.",
                EffectiveFrom = new DateTime(2022, 09, 01),
                EffectiveTo = null,
                Direction = PolicyDirection.Neutral,
                Source = "Ceylon Petroleum Corporation",
                ReferenceUrl = null,
                CreatedAtUtc = seededAt
            },
            new PolicyFlag
            {
                Id = Guid.Parse("a1f1c001-0000-0000-0000-000000000004"),
                PolicyType = PolicyType.ImportBan,
                Title = "Big onion & potato import restrictions",
                Description = "Import controls / suspension on big onions and potatoes to protect local growers around the harvest window, tightening domestic supply and lifting prices.",
                EffectiveFrom = new DateTime(2020, 07, 01),
                EffectiveTo = new DateTime(2021, 02, 28),
                Direction = PolicyDirection.Bullish,
                Source = "Department of Imports and Exports Control, Sri Lanka",
                ReferenceUrl = null,
                CreatedAtUtc = seededAt
            },
            new PolicyFlag
            {
                Id = Guid.Parse("a1f1c001-0000-0000-0000-000000000005"),
                PolicyType = PolicyType.PriceCeiling,
                Title = "Maximum retail price on rice varieties",
                Description = "Consumer Affairs Authority imposed maximum retail prices (price ceilings) on Nadu, Samba and Keeri Samba rice to curb retail inflation during the economic crisis.",
                EffectiveFrom = new DateTime(2023, 02, 13),
                EffectiveTo = new DateTime(2024, 01, 31),
                Direction = PolicyDirection.Bearish,
                Source = "Consumer Affairs Authority, Sri Lanka",
                ReferenceUrl = null,
                CreatedAtUtc = seededAt
            },
            new PolicyFlag
            {
                Id = Guid.Parse("a1f1c001-0000-0000-0000-000000000006"),
                PolicyType = PolicyType.FertiliserSubsidy,
                Title = "Fertiliser subsidy scheme continuation (2023/24)",
                Description = "Continued subsidised fertiliser distribution to paddy farmers for the 2023/24 Maha season, supporting normalised yields; still in effect.",
                EffectiveFrom = new DateTime(2023, 10, 01),
                EffectiveTo = null,
                Direction = PolicyDirection.Bearish,
                Source = "Ministry of Agriculture, Sri Lanka",
                ReferenceUrl = null,
                CreatedAtUtc = seededAt
            }
        );
    }
}
