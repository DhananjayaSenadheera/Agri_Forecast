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
    public DbSet<Market> Markets { get; set; }
    public DbSet<PriceObservation> PriceObservations { get; set; }

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
            // Next manual market code = MKT00000007 (7 seeded markets occupy 1..6).
            Mkt_Code = 7,
            Mkt_Padding = 8,
            Mkt_Prefix = "MKT",
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

        modelBuilder.Entity<Market>(e =>
        {
            // 50 chars: fits both the MKT###### seed codes and the back-fill twins keyed
            // 'ECOMAP-' + a 36-char GUID (= 43 chars); 20 was too narrow for the latter.
            e.Property(x => x.MarketCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.District).HasMaxLength(100);
            e.Property(x => x.MarketType).HasConversion<int>().IsRequired();

            // MarketCode is the human-facing business key — unique.
            e.HasIndex(x => x.MarketCode).IsUnique();
        });

        // Back-compat link: EconomicCenter -> Market (nullable, no cascade).
        // Restrict so a Market can never be deleted out from under an EconomicCenter;
        // existing rows stay valid because MarketId is nullable.
        modelBuilder.Entity<EconomicCenter>(e =>
        {
            e.HasOne<Market>()
                .WithMany()
                .HasForeignKey(x => x.MarketId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PriceObservation>(e =>
        {
            e.Property(x => x.ExternalCommodityName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Source).HasMaxLength(100).IsRequired();
            e.Property(x => x.ObservedDate).HasColumnType("date");

            // Prices decimal(10,2); arrivals decimal(12,2). All nullable.
            e.Property(x => x.WholesalePrice).HasPrecision(10, 2);
            e.Property(x => x.RetailPrice).HasPrecision(10, 2);
            e.Property(x => x.MinPrice).HasPrecision(10, 2);
            e.Property(x => x.MaxPrice).HasPrecision(10, 2);
            e.Property(x => x.ArrivalsKg).HasPrecision(12, 2);

            e.HasOne<Market>()
                .WithMany()
                .HasForeignKey(x => x.MarketId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<Crop>()
                .WithMany()
                .HasForeignKey(x => x.CropId)
                .OnDelete(DeleteBehavior.SetNull);

            // Idempotent upsert key. ExternalCommodityId is NULLABLE and SQL Server
            // treats NULLs as EQUAL in a unique index, which would collapse all
            // name-keyed (HARTI/CBSL) bulletins into one row. So we split into TWO
            // filtered unique indexes:
            //   * id-keyed sources (DEC) dedupe on ExternalCommodityId,
            //   * name-keyed sources dedupe on ExternalCommodityName,
            // guaranteeing at most one observation per commodity/market/date/source
            // in either regime without a sentinel value.
            e.HasIndex(x => new { x.MarketId, x.ExternalCommodityId, x.ObservedDate, x.Source })
                .IsUnique()
                .HasFilter("[ExternalCommodityId] IS NOT NULL")
                .HasDatabaseName("UX_PriceObservations_MarketCommodityIdDateSource");

            e.HasIndex(x => new { x.MarketId, x.ExternalCommodityName, x.ObservedDate, x.Source })
                .IsUnique()
                .HasFilter("[ExternalCommodityId] IS NULL")
                .HasDatabaseName("UX_PriceObservations_MarketCommodityNameDateSource");

            // Forecast read path: prices for a crop at a market over time.
            e.HasIndex(x => new { x.MarketId, x.CropId, x.ObservedDate })
                .HasDatabaseName("IX_PriceObservations_MarketCropDate");
        });

        SeedMarkets(modelBuilder);

    }

    // Deterministic seed of the initial market dimension: three physical DEC hubs
    // plus HARTI (Pettah wholesale, Narahenpita retail) and a CBSL national-aggregate
    // pseudo-market. Fixed Ids + fixed timestamps keep the seed idempotent across migrations.
    // Codes MKT00000001..MKT00000006 mirror the MKT###### scheme (DefaultSetting.Mkt_*).
    //
    // DEDUP TRAP (must be enforced downstream, NOT in schema): the seeded HARTI Pettah
    // wholesale market (MKT00000004) and a future ECOMAP twin of a legacy Colombo/Pettah
    // EconomicCenter could BOTH carry wholesale prices for the same location, double-counting
    // it in any cross-market average. Likewise the CBSL row is a NationalAggregate — an
    // already-averaged figure that must never be pooled with location-level markets.
    // The canonical-mapping layer MUST resolve overlapping physical locations to a single
    // market and exclude NationalAggregate markets from location-level aggregation BEFORE
    // any cross-market aggregation ships. Tracked on the ClickUp canonical-mapping task.
    private static void SeedMarkets(ModelBuilder modelBuilder)
    {
        var seededAt = new DateTime(2026, 07, 02, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<Market>().HasData(
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000001"),
                MarketCode = "MKT00000001",
                Name = "Dambulla Dedicated Economic Centre",
                District = (string?)"Matale",
                MarketType = MarketType.DEC,
                IsActive = true,
                CreatedAt = seededAt,
                UpdatedAt = seededAt
            },
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000002"),
                MarketCode = "MKT00000002",
                Name = "Keppetipola Dedicated Economic Centre",
                District = (string?)"Badulla",
                MarketType = MarketType.DEC,
                IsActive = true,
                CreatedAt = seededAt,
                UpdatedAt = seededAt
            },
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000003"),
                MarketCode = "MKT00000003",
                Name = "Thambuttegama Dedicated Economic Centre",
                District = (string?)"Anuradhapura",
                MarketType = MarketType.DEC,
                IsActive = true,
                CreatedAt = seededAt,
                UpdatedAt = seededAt
            },
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000004"),
                MarketCode = "MKT00000004",
                Name = "Pettah (HARTI wholesale)",
                District = (string?)"Colombo",
                MarketType = MarketType.Wholesale,
                IsActive = true,
                CreatedAt = seededAt,
                UpdatedAt = seededAt
            },
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000005"),
                MarketCode = "MKT00000005",
                Name = "Narahenpita (HARTI retail)",
                District = (string?)"Colombo",
                MarketType = MarketType.Retail,
                IsActive = true,
                CreatedAt = seededAt,
                UpdatedAt = seededAt
            },
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000006"),
                MarketCode = "MKT00000006",
                Name = "CBSL national average (pseudo-market)",
                District = (string?)null,
                MarketType = MarketType.NationalAggregate,
                IsActive = true,
                CreatedAt = seededAt,
                UpdatedAt = seededAt
            }
        );
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
