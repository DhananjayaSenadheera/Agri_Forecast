using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Database;

public class AgriForecastDbContext(DbContextOptions<AgriForecastDbContext> options) : DbContext(options) 
{
    public DbSet<Crop> Crops { get; set; }
    public DbSet<CropCategory> CropCategories { get; set; }
    public DbSet<CropAgronomyProfile> CropAgronomyProfiles { get; set; }
    public DbSet<EconomicCenter> EconomicCenters { get; set; }
    public DbSet<DefaultSetting> DefaultSettings { get; set; }
    public DbSet<MarketPrice> MarketPrices { get; set; }
    public DbSet<CropPrice> CropPrices { get; set; }
    public DbSet<WeatherRecord> WeatherRecords { get; set; }
    public DbSet<EconomicIndicator> EconomicIndicators { get; set; }
    public DbSet<MacroSeriesPoint> MacroSeriesPoints { get; set; }
    public DbSet<PolicyFlag> PolicyFlags { get; set; }
    public DbSet<FestivalCalendarEntry> FestivalCalendarEntries { get; set; }
    public DbSet<NewsEvent> NewsEvents { get; set; }
    public DbSet<NewsEventCrop> NewsEventCrops { get; set; }
    public DbSet<NewsEventMarket> NewsEventMarkets { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<RefreshTokenRecord> RefreshTokens { get; set; }
    public DbSet<Market> Markets { get; set; }
    public DbSet<PriceObservation> PriceObservations { get; set; }
    public DbSet<CommodityAlias> CommodityAliases { get; set; }
    public DbSet<IngestionWatermark> IngestionWatermarks { get; set; }
    public DbSet<IngestionRun> IngestionRuns { get; set; }
    public DbSet<IngestionVerification> IngestionVerifications { get; set; }
    public DbSet<ModelTrainingRun> ModelTrainingRuns { get; set; }
    public DbSet<ForecastSnapshot> ForecastSnapshots { get; set; }
    // Mapped to the singular table name UserCropWatchlist; the DbSet is plural only because the property
    // cannot share its name with the entity type. Same shape as UserActivityLog above.
    public DbSet<UserCropWatchlist> UserCropWatchlists { get; set; }
    public DbSet<UserCropWatchMarket> UserCropWatchMarkets { get; set; }
    public DbSet<PlantedDateRemoval> PlantedDateRemovals { get; set; }
    public DbSet<UserActivityEvent> UserActivityLog { get; set; }
    public DbSet<SystemError> SystemErrors { get; set; }

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

        // Refresh-token revocation store. UsedAtUtc / RevokedAtUtc are nullable (null = current / not
        // revoked) and are full datetime2 security-audit instants, not date-only ML dates.
        modelBuilder.Entity<RefreshTokenRecord>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasKey(x => x.Id);

            e.Property(x => x.Jti).IsRequired();
            e.Property(x => x.FamilyId).IsRequired();
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.IssuedAtUtc).IsRequired();
            e.Property(x => x.ExpiresAtUtc).IsRequired();

            // Jti is the rotation lookup key AND the dedup guarantee — unique.
            e.HasIndex(x => x.Jti).IsUnique();
            // Revoke-family scan (logout + reuse-detection theft response).
            e.HasIndex(x => x.FamilyId);
            // Revoke-all-for-user scan (admin delete/demote) + supports the FK.
            e.HasIndex(x => x.UserId);

            // CASCADE, deliberately unlike the Restrict posture on reference dimensions: a token row is a
            // per-user session artifact owned by the user, so deleting the user physically removes their
            // tokens — the strongest revocation, and a backstop for the explicit revoke in the handler.
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DefaultSetting>().HasData(new DefaultSetting
        {
            Id = 1,
            // Per-category-prefix crop-code counters, seeded to the next free value after the 96 existing
            // crops were re-coded; padding 6 gives VEG######/FRT######.
            Veg_Code = 71,
            Veg_Padding = 6,
            Veg_Prefix = CropCategory.VegetablePrefix,
            Frt_Code = 27,
            Frt_Padding = 6,
            Frt_Prefix = CropCategory.FruitPrefix,
            // Next manual market code is MKT00000013: the 12 seeded markets occupy 1..12, so runtime
            // registration can never re-issue a seeded code.
            Mkt_Code = 13,
            Mkt_Padding = 8,
            Mkt_Prefix = "MKT",
        });
        
        modelBuilder.Entity<CropCategory>(e =>
        {
            e.ToTable("CropCategories");
            e.HasKey(x => x.Id);

            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();

            // Self-FK for sub-categories. Restrict: a parent can never be deleted while a child references it.
            e.HasOne<CropCategory>()
                .WithMany()
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict);

            // Code is the human-facing business key — unique.
            e.HasIndex(x => x.Code).IsUnique();
        });

        SeedCropCategories(modelBuilder);

        modelBuilder.Entity<Crop>(e =>
        {
            // Optional grouping under a CropCategory. Restrict: a category cannot be deleted while a crop
            // references it.
            e.HasOne<CropCategory>()
                .WithMany()
                .HasForeignKey(x => x.CropCategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CropAgronomyProfile>(e =>
        {
            e.ToTable("CropAgronomyProfiles");
            e.HasKey(x => x.Id);

            e.Property(x => x.DataSource).HasMaxLength(500);

            // VerifiedOn is a curation record-date, stored date-only.
            e.Property(x => x.VerifiedOn).HasColumnType("date");

            // 1:1 with Crop; the unique CropId enforces one profile per crop. Restrict, like the other
            // crop-referencing FKs, so a Crop cannot be deleted while its profile references it.
            e.HasOne<Crop>()
                .WithMany()
                .HasForeignKey(x => x.CropId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.CropId).IsUnique();
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

        modelBuilder.Entity<MacroSeriesPoint>(e =>
        {
            e.Property(x => x.SeriesCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.Source).HasMaxLength(100).IsRequired();
            e.Property(x => x.Value).HasPrecision(18, 6);
            e.Property(x => x.IsPublishedAtImputed).IsRequired();

            // Both dates are date-only: ReferenceDate is the period described and PublishedAt is the vintage
            // the ML layer as-of-joins on, so neither may carry a hidden time.
            e.Property(x => x.ReferenceDate).HasColumnType("date").IsRequired();
            e.Property(x => x.PublishedAt).HasColumnType("date").IsRequired();

            // RetrievedAtUtc is a full datetime2 audit stamp (record-keeping only, never a feature).
            e.Property(x => x.RetrievedAtUtc).IsRequired();

            // One row per vintage of a period: a revised print carries a new PublishedAt and is a distinct
            // row, but the same (series, period, vintage) cannot be inserted twice.
            e.HasIndex(x => new { x.SeriesCode, x.ReferenceDate, x.PublishedAt }).IsUnique();

            // As-of read path: "latest vintage of a series knowable as of date D" scans PublishedAt.
            e.HasIndex(x => new { x.SeriesCode, x.PublishedAt });

            // Reference-axis read path: all vintages of a series for a period window.
            e.HasIndex(x => new { x.SeriesCode, x.ReferenceDate });
        });

        modelBuilder.Entity<PolicyFlag>(e =>
        {
            e.Property(x => x.PolicyType).HasConversion<int>().IsRequired();
            e.Property(x => x.Direction).HasConversion<int>().IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Source).HasMaxLength(200);
            e.Property(x => x.ReferenceUrl).HasMaxLength(500);

            // Date-only: these are the point-in-time keys the ML layer as-of-joins on.
            e.Property(x => x.EffectiveFrom).HasColumnType("date").IsRequired();
            e.Property(x => x.EffectiveTo).HasColumnType("date");

            // Primary lookup pattern is "what was active as-of date D", which scans EffectiveFrom.
            e.HasIndex(x => x.EffectiveFrom);
        });

        SeedPolicyFlags(modelBuilder);

        modelBuilder.Entity<FestivalCalendarEntry>(e =>
        {
            e.Property(x => x.FestivalKey).HasMaxLength(50).IsRequired();
            e.Property(x => x.Source).HasMaxLength(300);
            e.Property(x => x.LeadUpDays).IsRequired();
            e.Property(x => x.IsProvisional).IsRequired();

            // Date-only: it is the point-in-time key the ML layer as-of-joins on.
            e.Property(x => x.Date).HasColumnType("date").IsRequired();

            // One row per (festival, date) — a festival cannot be seeded twice on the same day.
            e.HasIndex(x => new { x.FestivalKey, x.Date }).IsUnique();

            // Primary read pattern is "which festivals fall near date D" → scans Date.
            e.HasIndex(x => x.Date);
        });

        SeedFestivalCalendar(modelBuilder);

        // News events: capture and storage only. Not an ML feature yet, so there is no seed data and no
        // training-data-warning idiom.
        modelBuilder.Entity<NewsEvent>(e =>
        {
            e.Property(x => x.EventType).HasConversion<int>().IsRequired();
            e.Property(x => x.Direction).HasConversion<int>().IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);

            // PublishedAt is the knowledge/vintage date, stored date-only so a future feature layer can
            // as-of-join on it. Immutable after create (the UpdateDto omits the field).
            e.Property(x => x.PublishedAt).HasColumnType("date").IsRequired();

            // Primary read pattern is reverse-chronological by knowledge date.
            e.HasIndex(x => x.PublishedAt);
        });

        modelBuilder.Entity<NewsEventCrop>(e =>
        {
            e.HasKey(x => new { x.NewsEventId, x.CropId });

            // Link is owned by the event → cascade-deleted with it.
            e.HasOne<NewsEvent>()
                .WithMany(n => n.AffectedCrops)
                .HasForeignKey(x => x.NewsEventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Crop side Restrict: a referenced Crop cannot be deleted until the link is removed.
            e.HasOne<Crop>()
                .WithMany()
                .HasForeignKey(x => x.CropId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.CropId);
        });

        modelBuilder.Entity<NewsEventMarket>(e =>
        {
            e.HasKey(x => new { x.NewsEventId, x.MarketId });

            e.HasOne<NewsEvent>()
                .WithMany(n => n.AffectedMarkets)
                .HasForeignKey(x => x.NewsEventId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne<Market>()
                .WithMany()
                .HasForeignKey(x => x.MarketId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.MarketId);
        });

        modelBuilder.Entity<CropPrice>(e =>
        {
            e.Property(x => x.AveragePrice).HasPrecision(18, 2);
            e.HasIndex(x => new { x.CropId, x.EconomicCenterId, x.Month }).IsUnique();

            // EconomicCenterId references Markets (a Dedicated Economic Centre is a Markets row with
            // IsEconomicCenter=1). Restrict so a Market cannot be deleted out from under a CropPrice row.
            e.HasOne(x => x.EconomicCenter)
                .WithMany()
                .HasForeignKey(x => x.EconomicCenterId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MarketPrice>(e =>
        {
            e.Property(x => x.MinPrice).HasPrecision(18, 2);
            e.Property(x => x.MaxPrice).HasPrecision(18, 2);

            // prevents duplicates even if worker runs twice
            e.HasIndex(x => new { x.Source, x.ExternalProductId, x.PriceDate })
                .IsUnique();

            // EconomicCenterId references Markets. MarketPrice has no navigation property, so the FK is
            // declared without one. Restrict so a Market cannot be deleted out from under a price row; the
            // column stays nullable because an unlinked source may exist before its backfill runs.
            e.HasOne<Market>()
                .WithMany()
                .HasForeignKey(x => x.EconomicCenterId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Market>(e =>
        {
            // 50 chars fits both the MKT###### seed codes and the 'ECOMAP-' + GUID backfill twins (43 chars).
            e.Property(x => x.MarketCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.District).HasMaxLength(100);
            e.Property(x => x.MarketType).HasConversion<int>().IsRequired();

            // NOT NULL, default false — existing and ingestion-provisioned markets stay plain markets until
            // explicitly promoted; the Dambulla DEC row is flagged by a MarketCode-keyed UPDATE.
            e.Property(x => x.IsEconomicCenter).IsRequired().HasDefaultValue(false);

            // Short display code (e.g. "DEC", "KEP"), NOT NULL, 8 chars. Display-only: it is never a key,
            // an FK or a join column, and nothing in the ML path reads it — everything keys on the
            // lowercase GUID Id, MarketCode stays the business key.
            e.Property(x => x.ShortCode).HasMaxLength(8).IsRequired().HasDefaultValue(string.Empty);

            // MarketCode is the human-facing business key — unique.
            e.HasIndex(x => x.MarketCode).IsUnique();

            // ShortCode is unique among ASSIGNED codes. The filter is load-bearing: a market registered
            // through POST api/markets/create without a display code stores '' (no abbreviation can be
            // derived safely from a name), and an unfiltered unique index would let the first such
            // registration block every later one. Every seeded market carries a code, so the filter never
            // hides a real duplicate.
            e.HasIndex(x => x.ShortCode)
                .IsUnique()
                .HasFilter("[ShortCode] <> ''")
                .HasDatabaseName("UX_Markets_ShortCode");
        });

        // Back-compat link EconomicCenter -> Market. Restrict so a Market cannot be deleted out from under an
        // EconomicCenter; existing rows stay valid because MarketId is nullable.
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

            // Unit quarantine: IsUnitConfirmed is NOT NULL and defaults false, so rows are held until
            // ingestion confirms the unit.
            e.Property(x => x.UnitRaw).HasMaxLength(50);
            e.Property(x => x.UnitConversionFactor).HasPrecision(10, 4);
            e.Property(x => x.IsUnitConfirmed).IsRequired().HasDefaultValue(false);

            e.HasOne<Market>()
                .WithMany()
                .HasForeignKey(x => x.MarketId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<Crop>()
                .WithMany()
                .HasForeignKey(x => x.CropId)
                .OnDelete(DeleteBehavior.SetNull);

            // Idempotent upsert key. ExternalCommodityId is nullable and SQL Server treats NULLs as EQUAL in
            // a unique index, which would collapse every name-keyed (HARTI/CBSL) bulletin into one row. So
            // there are TWO filtered unique indexes: id-keyed sources dedupe on ExternalCommodityId,
            // name-keyed sources on ExternalCommodityName.
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

        modelBuilder.Entity<CommodityAlias>(e =>
        {
            e.Property(x => x.Alias).HasMaxLength(200).IsRequired();
            e.Property(x => x.Source).HasMaxLength(100);
            e.Property(x => x.Language).HasMaxLength(20);
            e.Property(x => x.IsActive).IsRequired().HasDefaultValue(true);

            // Restrict: a Crop can never be deleted while an alias still maps to it.
            e.HasOne<Crop>()
                .WithMany()
                .HasForeignKey(x => x.CropId)
                .OnDelete(DeleteBehavior.Restrict);

            // Ambiguity guard: one alias must not map to two crops. SQL Server treats NULLs as EQUAL in a
            // unique index, which would collapse every global (Source IS NULL) alias into one row, so there
            // are two filtered unique indexes: source-scoped on (Alias, Source), global on (Alias).
            // Both are case-insensitive by the default collation, so "Beans" and "beans" cannot both exist.
            e.HasIndex(x => new { x.Alias, x.Source })
                .IsUnique()
                .HasFilter("[Source] IS NOT NULL")
                .HasDatabaseName("UX_CommodityAliases_AliasSource");

            e.HasIndex(x => x.Alias)
                .IsUnique()
                .HasFilter("[Source] IS NULL")
                .HasDatabaseName("UX_CommodityAliases_AliasGlobal");

            // Resolution read path: look up active aliases by (Alias, Source).
            e.HasIndex(x => new { x.Alias, x.Source, x.IsActive })
                .HasDatabaseName("IX_CommodityAliases_AliasSourceActive");
        });

        modelBuilder.Entity<IngestionWatermark>(e =>
        {
            e.Property(x => x.Source).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Property(x => x.LastMessage).HasMaxLength(1000);

            // Vintage high-water mark stored date-only.
            e.Property(x => x.LastObservedDate).HasColumnType("date");

            // One watermark per source — this is the business key the services resume on.
            e.HasIndex(x => x.Source).IsUnique();
        });

        // Ingestion RUN rows — one per source per pass. Enum-as-int, date-only coverage columns and a
        // 1000-char cap on the sanitized error, mirroring the IngestionWatermark config.
        modelBuilder.Entity<IngestionRun>(e =>
        {
            e.Property(x => x.Source).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Property(x => x.ErrorSummary).HasMaxLength(1000);

            // Coverage window is date-only (no hidden time) — mirrors the reference-entity discipline.
            e.Property(x => x.CoveredFromDate).HasColumnType("date");
            e.Property(x => x.CoveredToDate).HasColumnType("date");

            // Primary read path: "latest runs for a source", newest first.
            e.HasIndex(x => new { x.Source, x.StartedUtc })
                .IsDescending(false, true)
                .HasDatabaseName("IX_IngestionRuns_SourceStartedUtc");

            // Reconstruct a whole pass by its BatchId.
            e.HasIndex(x => x.BatchId);
        });

        // Ingestion VERIFICATION rows — one per verification run. Written by Python; .NET owns the schema.
        modelBuilder.Entity<IngestionVerification>(e =>
        {
            // The raw per-check JSON is guarded by an ISJSON check constraint so a malformed blob can never
            // persist.
            e.ToTable(t => t.HasCheckConstraint(
                "CK_IngestionVerifications_ChecksJson_IsJson",
                "ISJSON([ChecksJson]) = 1"));

            e.Property(x => x.OverallStatus).HasConversion<int>().IsRequired();
            e.Property(x => x.ChecksJson).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(1000);

            // Pipeline/business date is date-only (no hidden time).
            e.Property(x => x.PipelineDate).HasColumnType("date").IsRequired();

            // Primary read path: most-recent verifications first.
            e.HasIndex(x => x.RunUtc)
                .IsDescending()
                .HasDatabaseName("IX_IngestionVerifications_RunUtc");

            // Link a verification to its pass.
            e.HasIndex(x => x.BatchId);
        });

        // Model TRAINING run rows — one per training run. Written by Python; .NET owns the schema. Version is
        // the unique business key, the MAE columns get explicit precision, and CreatedUtc is DB-defaulted so a
        // Python INSERT that omits it still stamps a creation instant.
        modelBuilder.Entity<ModelTrainingRun>(e =>
        {
            e.Property(x => x.Version).HasMaxLength(20).IsRequired();
            e.Property(x => x.PromotionDecision).HasMaxLength(2000);
            e.Property(x => x.BestMlKind).HasMaxLength(50);
            e.Property(x => x.BestBaselineKind).HasMaxLength(50);
            e.Property(x => x.FeatureContractHash).HasMaxLength(100);

            e.Property(x => x.BestMlMae).HasPrecision(10, 2);
            e.Property(x => x.BestBaselineMae).HasPrecision(10, 2);

            e.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");

            // One row per version.
            e.HasIndex(x => x.Version)
                .IsUnique()
                .HasDatabaseName("IX_ModelTrainingRuns_Version");

            // Primary read path: most-recent training runs first.
            e.HasIndex(x => x.TrainedAtUtc)
                .IsDescending()
                .HasDatabaseName("IX_ModelTrainingRuns_TrainedAtUtc");
        });

        // FORECAST SNAPSHOT rows — one frozen prediction per crop per day. Written by the Python nightly
        // job (insert at snapshot, single update at maturity); .NET owns the schema and reads them.
        // Date-only columns carry no hidden time, every money column gets explicit precision, and
        // CreatedAtUtc is DB-defaulted so a Python INSERT that omits it still stamps a creation instant.
        modelBuilder.Entity<ForecastSnapshot>(e =>
        {
            // The entity's factory guards do NOT run on the production write path — the Python job writes
            // these rows with raw SQL — so the two invariants that must never be violated live in the DB
            // itself. Mirrors the ISJSON guard on IngestionVerifications.
            e.ToTable(t =>
            {
                // Keep in lockstep with ForecastSnapshotMaturityStates. An unknown state would otherwise
                // sit invisible: it matches no filter, so the maturing sweep would skip the row forever.
                //
                // COLLATE Latin1_General_BIN2 forces an EXACT, case-sensitive match. The database's own
                // collation is case-insensitive, so without it 'Pending' would be accepted — harmless to
                // SQL, but Python compares these strings case-SENSITIVELY, so such a row would silently
                // vanish from the accuracy aggregates. Verified live: 'Pending' is rejected, 'pending' is
                // not.
                t.HasCheckConstraint(
                    "CK_ForecastSnapshots_MaturityState",
                    "[MaturityState] COLLATE Latin1_General_BIN2 IN ('pending', 'matured', 'actual_unavailable', 'not_maturable')");

                // A band may be clipped at zero but may never be inverted — an inverted band would make
                // WithinInterval meaningless and render a nonsense range to the farmer.
                t.HasCheckConstraint(
                    "CK_ForecastSnapshots_Band",
                    "[UpperBound] >= [LowerBound] AND [LowerBound] >= 0");
            });

            e.Property(x => x.SnapshotDate).HasColumnType("date").IsRequired();
            e.Property(x => x.HarvestDate).HasColumnType("date");
            e.Property(x => x.ActualObservedDate).HasColumnType("date");

            e.Property(x => x.PredictedPrice).HasPrecision(10, 2).IsRequired();
            e.Property(x => x.LowerBound).HasPrecision(10, 2).IsRequired();
            e.Property(x => x.UpperBound).HasPrecision(10, 2).IsRequired();
            e.Property(x => x.ReferencePrice).HasPrecision(10, 2);
            e.Property(x => x.ActualPrice).HasPrecision(10, 2);
            e.Property(x => x.SignedError).HasPrecision(10, 2);
            e.Property(x => x.AbsoluteError).HasPrecision(10, 2);
            e.Property(x => x.PercentageError).HasPrecision(9, 4);

            e.Property(x => x.Confidence).HasMaxLength(20).IsRequired();
            e.Property(x => x.ActivePredictor).HasMaxLength(50).IsRequired();
            e.Property(x => x.FallbackTier).HasMaxLength(50);
            e.Property(x => x.ModelVersion).HasMaxLength(20);
            e.Property(x => x.ReasonCode).HasMaxLength(100);

            // Stored as a STRING, not the usual enum-as-int: the Python job writes and filters these rows
            // in raw SQL and the pending index below is literally WHERE MaturityState = 'pending'.
            // Values are pinned in ForecastSnapshotMaturityStates.
            e.Property(x => x.MaturityState).HasMaxLength(30).IsRequired();

            e.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");

            // Restrict: a Crop can never be deleted while its forecast history still exists.
            e.HasOne<Crop>()
                .WithMany()
                .HasForeignKey(x => x.CropId)
                .OnDelete(DeleteBehavior.Restrict);

            // One row per crop per day — the key the nightly upsert is idempotent on. ModelVersion is
            // deliberately NOT part of it: a day belongs to whichever version served it.
            // NOTE the named HasIndex overloads: two indexes share the (CropId, SnapshotDate) property
            // pair, and the unnamed overload would return the SAME index builder for both and silently
            // collapse them into one.
            e.HasIndex(x => new { x.CropId, x.SnapshotDate }, "UX_ForecastSnapshots_CropSnapshotDate")
                .IsUnique();

            // Maturing sweep hot path: due rows only. Filtered so the index stays tiny as history grows.
            e.HasIndex(x => x.HarvestDate, "IX_ForecastSnapshots_HarvestDatePending")
                .HasFilter("[MaturityState] = 'pending'");

            // Dashboard read path: "latest snapshot per crop", newest first.
            e.HasIndex(x => new { x.CropId, x.SnapshotDate }, "IX_ForecastSnapshots_CropSnapshotDateDesc")
                .IsDescending(false, true);
        });

        // FARMER WATCHLIST rows — one per (user, crop) the farmer has added to "my crops". Personal data:
        // owner-scoped in every query, never aggregated across users, never read by the ML layer.
        modelBuilder.Entity<UserCropWatchlist>(e =>
        {
            e.ToTable("UserCropWatchlist");

            // CASCADE from Users: deleting an account takes its watchlist with it. Nothing here outlives
            // its owner, and an orphan row would be personal data with nobody to scope it to.
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // RESTRICT on Crops: reference data a farmer is actively watching cannot be deleted out from
            // under them, and the delete fails loudly instead of silently emptying a watchlist. The FK is
            // explicitly NoAction/Restrict rather than defaulted, because SQL Server would otherwise reject
            // multiple cascade paths into this table.
            e.HasOne<Crop>()
                .WithMany()
                .HasForeignKey(x => x.CropId)
                .OnDelete(DeleteBehavior.Restrict);

            // The farmer's own planting day. DATE, not datetime2: a planting day has no time component,
            // and a hidden 00:00:00 would make "today" ambiguous across timezones.
            e.Property(x => x.PlantedDate).HasColumnType("date");

            e.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.UpdatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");

            // The watched markets of this crop, loaded through the backing field so the collection stays
            // read-only on the entity: the cap and the no-duplicates rule live in the domain methods, and a
            // settable navigation would be a way around both.
            e.HasMany(x => x.Markets)
                .WithOne()
                .HasForeignKey(m => m.UserCropWatchlistId)
                .OnDelete(DeleteBehavior.Cascade);

            e.Metadata.FindNavigation(nameof(UserCropWatchlist.Markets))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);

            // One row per crop per farmer. Also the read path for "everything this user watches": UserId is
            // the leading column, so the user-scoped list seeks on this index rather than scanning.
            e.HasIndex(x => new { x.UserId, x.CropId }, "UX_UserCropWatchlist_UserCrop")
                .IsUnique();
        });

        // The markets a farmer follows FOR ONE watched crop — children of UserCropWatchlist. Personal data
        // by inheritance: the row has no UserId of its own and is only ever reachable through its
        // (already user-scoped) parent.
        modelBuilder.Entity<UserCropWatchMarket>(e =>
        {
            e.ToTable("UserCropWatchMarkets");

            // RESTRICT on Markets, with no inverse navigation: a market a farmer is watching cannot be
            // deleted out from under them, and Market gains no navigation into personal data. The parent FK
            // (Cascade) is configured from the UserCropWatchlist side above; declaring it here as well
            // would give EF two relationships for one column.
            e.HasOne<Market>()
                .WithMany()
                .HasForeignKey(x => x.MarketId)
                .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");

            // One row per (watched crop, market). The 3-market cap is deliberately NOT a DB constraint —
            // it is enforced in the domain and answered as the too_many_markets wire code — but "the same
            // market twice" is a data error, so the database refuses it outright.
            e.HasIndex(x => new { x.UserCropWatchlistId, x.MarketId }, "UX_UserCropWatchMarkets_EntryMarket")
                .IsUnique();
        });

        // PLANTING-DATE REMOVAL rows — one per time a farmer cleared a recorded planting date, written INSIDE
        // the same transaction as the clear itself. Append-only and personal data, like the watchlist row it
        // refers to. Reason is enum-as-int and those values are persisted; Note is the farmer's own short
        // free text and is the ONE free-text column here (never copied into UserActivityLog.Details).
        modelBuilder.Entity<PlantedDateRemoval>(e =>
        {
            e.ToTable("PlantedDateRemovals");

            // CASCADE from Users, matching UserCropWatchlist: the record of a farmer's own plantings does not
            // outlive the account, and an orphan row would be personal data with nobody to scope it to.
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // RESTRICT on Crops, again matching the watchlist: reference data a record refers to cannot be
            // deleted out from under it, and the delete fails loudly rather than shredding the history.
            e.HasOne<Crop>()
                .WithMany()
                .HasForeignKey(x => x.CropId)
                .OnDelete(DeleteBehavior.Restrict);

            // DATE, not datetime2 — the cleared value was a planting DAY, and a hidden 00:00:00 would make it
            // timezone-dependent exactly as it would on UserCropWatchlist.PlantedDate.
            e.Property(x => x.RemovedPlantedDate).HasColumnType("date").IsRequired();

            e.Property(x => x.Reason).HasConversion<int>().IsRequired();
            e.Property(x => x.Note).HasMaxLength(PlantedDateRemoval.NoteMaxLength);

            // No SYSUTCDATETIME() default: the factory always stamps OccurredUtc and only .NET writes this
            // table, so a DB default would only hide a caller that forgot the clock.
            e.Property(x => x.OccurredUtc).IsRequired();

            // The read path this table exists to serve one day: one farmer's removals for one crop, newest
            // first. Nothing queries it yet — the index is here because the column order is decided by that
            // query, not by the insert.
            e.HasIndex(x => new { x.UserId, x.CropId, x.OccurredUtc },
                    "IX_PlantedDateRemovals_UserCropOccurredUtc")
                .IsDescending(false, false, true);
        });

        // User ACTIVITY rows — one per account or content event, written by IUserActivityAudit. EventType is
        // enum-as-int and those values are persisted. Only UsernameAttempted and Details are free text and
        // both are length-capped; no password, token or body is ever stored.
        modelBuilder.Entity<UserActivityEvent>(e =>
        {
            e.ToTable("UserActivityLog");

            e.Property(x => x.EventType).HasConversion<int>().IsRequired();
            e.Property(x => x.UsernameAttempted).HasMaxLength(100);
            e.Property(x => x.Details).HasMaxLength(500);

            // Primary read path: most-recent events first.
            e.HasIndex(x => x.OccurredUtc)
                .IsDescending()
                .HasDatabaseName("IX_UserActivityLog_OccurredUtc");
        });

        // SYSTEM ERROR rows — one per unhandled 500, written by ISystemErrorLog. OccurredUtc is DB-defaulted.
        // StackTrace is nvarchar(max) though the factory caps it to 8000. Only the exception type, message
        // and stack plus the request method, path and trace id are stored — but Message and StackTrace are
        // verbatim exception text, so see the SystemError entity's privacy note.
        modelBuilder.Entity<SystemError>(e =>
        {
            e.Property(x => x.Source).HasMaxLength(20).IsRequired();
            e.Property(x => x.ExceptionType).HasMaxLength(200).IsRequired();
            e.Property(x => x.Message).HasMaxLength(1000);
            e.Property(x => x.StackTrace); // nvarchar(max) — factory caps to 8000 chars
            e.Property(x => x.Path).HasMaxLength(200);
            e.Property(x => x.Method).HasMaxLength(10);
            e.Property(x => x.TraceId).HasMaxLength(50);

            e.Property(x => x.OccurredUtc).HasDefaultValueSql("SYSUTCDATETIME()");

            // Primary read path: most-recent errors first.
            e.HasIndex(x => x.OccurredUtc)
                .IsDescending()
                .HasDatabaseName("IX_SystemErrors_OccurredUtc");
        });

        SeedMarkets(modelBuilder);

    }

    // Deterministic seed of the initial market dimension: three physical DEC hubs plus HARTI (Pettah
    // wholesale, Narahenpita retail) and a CBSL national-aggregate pseudo-market. Fixed Ids and timestamps
    // keep the seed idempotent across migrations; codes follow the MKT###### scheme.
    //
    // Dedup trap, which must be enforced downstream and not in the schema: the seeded HARTI Pettah market
    // and a future ECOMAP twin of a legacy Colombo/Pettah EconomicCenter could both carry wholesale prices
    // for the same location, double-counting it in a cross-market average. The CBSL row is a
    // NationalAggregate — an already-averaged figure that must never be pooled with location-level markets.
    // The canonical-mapping layer must resolve overlapping locations to one market and exclude
    // NationalAggregate markets before any cross-market aggregation ships.
    private static void SeedMarkets(ModelBuilder modelBuilder)
    {
        var seededAt = new DateTime(2026, 07, 02, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<Market>().HasData(
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000001"),
                MarketCode = "MKT00000001",
                ShortCode = "DEC",
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
                ShortCode = "KEP",
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
                ShortCode = "THB",
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
                ShortCode = "PET",
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
                ShortCode = "NAR",
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
                ShortCode = "NAT",
                Name = "CBSL national average (pseudo-market)",
                District = (string?)null,
                MarketType = MarketType.NationalAggregate,
                IsActive = true,
                CreatedAt = seededAt,
                UpdatedAt = seededAt
            },
            // 6 additional HARTI bulletin markets, added in lockstep with the parser widening.
            // Classification is owner-verified best evidence: Meegoda / Nuwara Eliya / Veyangoda are
            // formally-designated Dedicated Economic Centres; Kandy / Norochchole / Bandarawela are municipal
            // wholesale markets. Norochchole is the least certain and is reclassifiable.
            //
            // IsEconomicCenter is deliberately not set here: only Dambulla (MKT00000001) carries it today,
            // even though Keppetipola and Thambuttegama are MarketType.DEC. MarketType classifies the kind of
            // market; IsEconomicCenter=1 flags the single feature-reference DEC.
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000007"),
                MarketCode = "MKT00000007",
                ShortCode = "KAN",
                Name = "Kandy (HARTI wholesale)",
                District = (string?)"Kandy",
                MarketType = MarketType.Wholesale,
                IsActive = true,
                CreatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc)
            },
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000008"),
                MarketCode = "MKT00000008",
                ShortCode = "MEE",
                Name = "Meegoda Dedicated Economic Centre",
                District = (string?)"Colombo",
                MarketType = MarketType.DEC,
                IsActive = true,
                CreatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc)
            },
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000009"),
                MarketCode = "MKT00000009",
                ShortCode = "NOR",
                // Best-evidence classification: a municipal wholesale market rather than a designated DEC,
                // and the least certain of the three.
                Name = "Norochchole (HARTI wholesale)",
                District = (string?)"Puttalam",
                MarketType = MarketType.Wholesale,
                IsActive = true,
                CreatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc)
            },
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000010"),
                MarketCode = "MKT00000010",
                ShortCode = "NUW",
                Name = "Nuwara Eliya Dedicated Economic Centre",
                District = (string?)"Nuwara Eliya",
                MarketType = MarketType.DEC,
                IsActive = true,
                CreatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc)
            },
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000011"),
                MarketCode = "MKT00000011",
                ShortCode = "BAN",
                Name = "Bandarawela (HARTI wholesale)",
                District = (string?)"Badulla",
                MarketType = MarketType.Wholesale,
                IsActive = true,
                CreatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc)
            },
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000012"),
                MarketCode = "MKT00000012",
                ShortCode = "VEY",
                Name = "Veyangoda Dedicated Economic Centre",
                District = (string?)"Gampaha",
                MarketType = MarketType.DEC,
                IsActive = true,
                CreatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc)
            }
        );
    }

    // CROP CATEGORIES — a reference dimension with a manual update path (no ingestion, CQRS or endpoint).
    // Fixed lowercase GUIDs and a fixed CreatedAt keep the seed deterministic and idempotent; a UtcNow here
    // would churn the migrations diff every build. Mirrors the HARTI grouping: top-level Vegetable / Fruit
    // plus Up-country / Low-country Vegetable sub-categories whose ParentId points at Vegetable.
    //
    // Never HasData on Crop rows — crops are auto-provisioned at runtime with per-database GUIDs, so
    // assigning categories to existing crops is a separate name-keyed backfill.
    //
    // To add a category: add a seed row with a new fixed lowercase GUID and a unique Code, then add a
    // migration and apply it.
    private static void SeedCropCategories(ModelBuilder modelBuilder)
    {
        var seededAt = new DateTime(2026, 07, 05, 0, 0, 0, DateTimeKind.Utc);

        var vegetableId = Guid.Parse("d4c40001-0000-0000-0000-000000000001");
        var fruitId = Guid.Parse("d4c40001-0000-0000-0000-000000000002");

        modelBuilder.Entity<CropCategory>().HasData(
            new CropCategory
            {
                Id = vegetableId,
                Code = "VEG",
                Name = "Vegetable",
                ParentId = null,
                CreatedAt = seededAt
            },
            new CropCategory
            {
                Id = fruitId,
                Code = "FRT",
                Name = "Fruit",
                ParentId = null,
                CreatedAt = seededAt
            },
            new CropCategory
            {
                Id = Guid.Parse("d4c40001-0000-0000-0000-000000000003"),
                Code = "VEG-UP",
                Name = "Up-country Vegetable",
                ParentId = vegetableId,
                CreatedAt = seededAt
            },
            new CropCategory
            {
                Id = Guid.Parse("d4c40001-0000-0000-0000-000000000004"),
                Code = "VEG-LOW",
                Name = "Low-country Vegetable",
                ParentId = vegetableId,
                CreatedAt = seededAt
            }
        );
    }

    // Real Sri Lankan national policies captured point-in-time for the ML feature store. Fixed Ids and a
    // fixed CreatedAtUtc keep the seed deterministic and idempotent. Dates are date-only.
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

    // FESTIVAL CALENDAR — yearly-static reference data with a manual annual update path (no ingestion
    // service, CQRS or endpoint). Seeded via HasData with fixed GUIDs and a fixed CreatedAtUtc; a UtcNow
    // here would churn the migrations diff every build. This table is the single source of truth — the
    // Python feature layer reads it via load_festivals() — so do not add a static festival-days twin.
    //
    // To update (annual gazette check, around November each year):
    //   1. Get the next year's dates from the Department of Government Printing holiday gazette.
    //   2. Flip that year's AVURUDU / THAI_PONGAL rows from IsProvisional=true to false, set the real
    //      gazette Source citation, and correct the Date if the gazette differs from the estimate.
    //   3. Extend the seed forward by one year so it always covers 2015 (training-history start) to
    //      current+N. A forward-only seed silently leaves most training rows with no festival signal while
    //      cross-validation still looks fine.
    //   4. Add a migration and apply it.
    //
    // EID_UL_FITR / EID_UL_ADHA / DEEPAVALI are intentionally not seeded: their dates need moon-sighting or
    // per-year lunar verification and cannot be confirmed offline. Add them once gazette-confirmed.
    //
    // Seed span 2015..2030 inclusive.
    //   AVURUDU     — the Apr 13 (eve) + Apr 14 (day) pair each year. The lead-up window anchors on the
    //                 Apr 13 row; Apr 14 carries LeadUpDays=0 so it is not double-counted. 2015-2026 are
    //                 confirmed, 2027-2030 provisional.
    //   CHRISTMAS   — Dec 25, fixed date, confirmed for every year.
    //   THAI_PONGAL — Jan 14 (occasionally Jan 15), marked provisional where the gazette date could not be
    //                 cited here. Folded in because the old hardcoded Python check covered it.
    private static void SeedFestivalCalendar(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FestivalCalendarEntry>().HasData(GetFestivalCalendarSeed());
    }

    // Deterministic, wall-clock-free seed rows. Public so tests can assert on the exact rows HasData gets.
    public static IReadOnlyList<FestivalCalendarEntry> GetFestivalCalendarSeed()
    {
        // Fixed recording timestamp — never UtcNow (would churn the migrations diff every build).
        var seededAt = new DateTime(2026, 07, 03, 0, 0, 0, DateTimeKind.Utc);

        // Inclusive seed span. Start = 2015 (training history begins 2015-06-22); end = 2030.
        const int firstYear = 2015;
        const int lastYear = 2030;

        // AVURUDU / THAI_PONGAL confirmed against the gazette through this year; later = provisional.
        const int lastConfirmedYear = 2026;

        var rows = new List<FestivalCalendarEntry>();

        for (var year = firstYear; year <= lastYear; year++)
        {
            var confirmed = year <= lastConfirmedYear;
            var gazette = $"Department of Government Printing, Sri Lanka — annual holiday gazette {year}";

            // AVURUDU (Sinhala & Tamil New Year): the Apr 13 eve row anchors the lead-up window.
            rows.Add(new FestivalCalendarEntry
            {
                Id = FestivalId(0xA013, year),
                FestivalKey = "AVURUDU",
                Date = new DateTime(year, 04, 13),
                LeadUpDays = 14,
                IsProvisional = !confirmed,
                Source = confirmed ? gazette : null,
                CreatedAtUtc = seededAt
            });
            // Apr 14 is the paired day; LeadUpDays=0 so the window is not double-counted.
            rows.Add(new FestivalCalendarEntry
            {
                Id = FestivalId(0xA014, year),
                FestivalKey = "AVURUDU",
                Date = new DateTime(year, 04, 14),
                LeadUpDays = 0,
                IsProvisional = !confirmed,
                Source = confirmed ? gazette : null,
                CreatedAtUtc = seededAt
            });

            // THAI_PONGAL: Jan 14, provisional where it could not be gazette-cited here.
            rows.Add(new FestivalCalendarEntry
            {
                Id = FestivalId(0x7014, year),
                FestivalKey = "THAI_PONGAL",
                Date = new DateTime(year, 01, 14),
                LeadUpDays = 14,
                IsProvisional = !confirmed,
                Source = confirmed ? gazette : null,
                CreatedAtUtc = seededAt
            });

            // CHRISTMAS: Dec 25, fixed date, confirmed for all years.
            rows.Add(new FestivalCalendarEntry
            {
                Id = FestivalId(0xC025, year),
                FestivalKey = "CHRISTMAS",
                Date = new DateTime(year, 12, 25),
                LeadUpDays = 14,
                IsProvisional = false,
                Source = "Fixed Gregorian date (Dec 25)",
                CreatedAtUtc = seededAt
            });
        }

        return rows;
    }

    // Deterministic fixed GUID per (festival tag, year). Constant-folded at model build, so EF snapshots
    // literal GUIDs that stay stable across migrations. The {year:0000} embeds decimal digits in a hex GUID
    // group — valid only because 0-9 are hex-safe. Do not reuse this pattern with non-digit values.
    private static Guid FestivalId(int tag, int year)
        => Guid.Parse($"c3f30001-0000-0000-{tag:x4}-00000000{year:0000}");
}
