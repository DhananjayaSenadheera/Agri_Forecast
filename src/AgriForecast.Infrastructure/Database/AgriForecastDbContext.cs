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

        // Refresh-token revocation store (jti / token-family). Additive, net-new table — no seed,
        // no backfill. UsedAtUtc / RevokedAtUtc are nullable (null = current / not revoked); the
        // timestamps are full datetime2 security-audit instants, NOT date-only ML reference dates.
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

            // CASCADE (deliberate deviation from the prevailing Restrict posture on reference
            // dimensions): a RefreshTokenRecord is a per-user session artifact OWNED by the user,
            // not shared reference data. Deleting a user physically removes their token rows — the
            // strongest possible revocation, and a defence-in-depth backstop even if the explicit
            // RevokeAllForUserAsync in the delete handler were ever removed.
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DefaultSetting>().HasData(new DefaultSetting
        {
            Id = 1,
            // R2 D-DF4: per-category-prefix crop-code counters. Seeded to next-free after the 96
            // existing crops were re-coded (VEG000001..VEG000070 ⇒ next 71; FRT000001..FRT000026
            // ⇒ next 27); padding 6 → VEG######/FRT######.
            Veg_Code = 71,
            Veg_Padding = 6,
            Veg_Prefix = CropCategory.VegetablePrefix,
            Frt_Code = 27,
            Frt_Padding = 6,
            Frt_Prefix = CropCategory.FruitPrefix,
            // Next manual market code = MKT00000013 (12 seeded markets occupy 1..12 after
            // R2 Step 6.2 added MKT00000007..MKT00000012). Bumped 7 → 13 so runtime market
            // registration (CodeSettings.GetMktCode) can never re-issue a seeded code.
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

            // Self-FK for sub-categories. Restrict: a parent category can never be
            // deleted while a child still references it.
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
            // Optional grouping under a CropCategory. Restrict: a category can never be
            // deleted while a crop still references it. Nullable until the later backfill.
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

            // VerifiedOn is a curation record-date, stored date-only (no hidden time) —
            // mirrors the date-only discipline used across the reference entities.
            e.Property(x => x.VerifiedOn).HasColumnType("date");

            // 1:1 with Crop. Unique CropId enforces one agronomy profile per crop. Restrict:
            // a Crop can never be deleted while its profile references it (mirrors the
            // CommodityAlias -> Crop Restrict FK; agronomy is owned reference data, not a
            // detach-on-delete child).
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

            // Both dates stored date-only (no time) — ReferenceDate is the period described and
            // PublishedAt is the vintage/KnowledgeDate the ML layer as-of-joins on; neither may
            // carry a hidden time (mirrors PolicyFlag / EconomicIndicator date discipline).
            e.Property(x => x.ReferenceDate).HasColumnType("date").IsRequired();
            e.Property(x => x.PublishedAt).HasColumnType("date").IsRequired();

            // RetrievedAtUtc is a full datetime2 audit stamp (record-keeping only, never a feature).
            e.Property(x => x.RetrievedAtUtc).IsRequired();

            // One row per VINTAGE of a period: a revised print carries a new PublishedAt and is a
            // distinct row, but the same (series, period, vintage) may not be inserted twice. Keeps
            // ingestion idempotent at the DB level; matches ExistsAsync's triple key.
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

            // Dates stored date-only (no time) — these are the point-in-time keys
            // the ML layer as-of-joins on, so they must never carry a hidden time.
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

            // Date stored date-only (no time) — it is the point-in-time key the ML layer
            // as-of-joins on, so it must never carry a hidden time (mirrors PolicyFlag).
            e.Property(x => x.Date).HasColumnType("date").IsRequired();

            // One row per (festival, date) — a festival cannot be seeded twice on the same day
            // (idempotency / dedup at the DB level; mirrors EconomicIndicator's unique index).
            e.HasIndex(x => new { x.FestivalKey, x.Date }).IsUnique();

            // Primary read pattern is "which festivals fall near date D" → scans Date.
            e.HasIndex(x => x.Date);
        });

        SeedFestivalCalendar(modelBuilder);

        // News events (ADM-7): capture + storage only. NOT yet an ML feature, so — unlike PolicyFlag
        // / FestivalCalendarEntry — no seed data and no training-data-warning idiom hang off it.
        modelBuilder.Entity<NewsEvent>(e =>
        {
            e.Property(x => x.EventType).HasConversion<int>().IsRequired();
            e.Property(x => x.Direction).HasConversion<int>().IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.SourceUrl).HasMaxLength(1000);

            // PublishedAt = the knowledge/as-of/vintage date. Stored date-only (no hidden time) so a
            // future feature layer can as-of-join on it cleanly; immutable after create (enforced by
            // the UpdateDto omitting the field). Mirrors MacroSeriesPoint.PublishedAt discipline.
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

            // Crop side Restrict: a referenced Crop cannot be deleted until the link is removed
            // (uniform with the CommodityAlias / CropAgronomyProfile / CropPrice dimension FKs).
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

            // R2 D-DF3: EconomicCenterId now references Markets (the EconomicCenters CRUD dimension
            // retired; a Dedicated Economic Centre is a Markets row with IsEconomicCenter=1).
            // Restrict so a Market can never be deleted out from under a CropPrice row.
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

            // R2 Step 3.3 / D-DF3: EconomicCenterId references Markets (the EconomicCenters
            // CRUD dimension is retired; a Dedicated Economic Centre is a Markets row with
            // IsEconomicCenter=1). MarketPrice has no nav property, so the FK is declared
            // without a reference navigation. Restrict so a Market can never be deleted out
            // from under a price row. Column stays nullable in the DB (an unlinked source
            // may exist before its per-source backfill runs).
            e.HasOne<Market>()
                .WithMany()
                .HasForeignKey(x => x.EconomicCenterId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Market>(e =>
        {
            // 50 chars: fits both the MKT###### seed codes and the back-fill twins keyed
            // 'ECOMAP-' + a 36-char GUID (= 43 chars); 20 was too narrow for the latter.
            e.Property(x => x.MarketCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.District).HasMaxLength(100);
            e.Property(x => x.MarketType).HasConversion<int>().IsRequired();

            // IsEconomicCenter folds the retiring EconomicCenters dimension into Markets
            // (R2 D-DF3). NOT NULL, default false — existing rows and ingestion-provisioned
            // markets stay plain markets until explicitly promoted; the Dambulla DEC row is
            // flagged in the same migration via a MarketCode-keyed UPDATE.
            e.Property(x => x.IsEconomicCenter).IsRequired().HasDefaultValue(false);

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

            // Unit quarantine (R1.1 P1). UnitRaw nullable; UnitConversionFactor decimal(10,4)
            // nullable; IsUnitConfirmed NOT NULL default false (fail-closed — rows are
            // quarantined until ingestion confirms the unit). The 0-row table needs no back-fill.
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

            // Ambiguity guard: one alias must not map to two crops. SQL Server treats NULLs
            // as EQUAL in a unique index, which would collapse every global (Source IS NULL)
            // alias into one row — so we split into TWO filtered unique indexes, exactly as
            // PriceObservation splits its id/name-keyed dedup:
            //   * source-scoped aliases dedupe on (Alias, Source) where Source IS NOT NULL,
            //   * global aliases dedupe on (Alias) where Source IS NULL.
            // Both are case-INSENSITIVE (SQL Server default collation) — desirable here, so
            // "Beans"/"beans" cannot be inserted as two conflicting mappings.
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

            // Vintage high-water mark stored date-only (no hidden time) — mirrors the
            // date-only discipline on PriceObservation.ObservedDate / PolicyFlag effective dates.
            e.Property(x => x.LastObservedDate).HasColumnType("date");

            // One watermark per source — this is the business key the services resume on.
            e.HasIndex(x => x.Source).IsUnique();
        });

        // Ingestion RUN rows — one per source per pass. Net-new, additive table (no seed, no
        // backfill). Mirrors the IngestionWatermark config discipline: enum-as-int, date-only
        // coverage columns, 1000-char message cap on the sanitized error.
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

        // Ingestion VERIFICATION rows — one per verification run. WRITTEN BY PYTHON later; .NET owns
        // the SCHEMA only here. Net-new, additive (no seed, no backfill).
        modelBuilder.Entity<IngestionVerification>(e =>
        {
            // Raw per-check JSON is guarded by an ISJSON check constraint so a malformed blob can
            // never persist. nvarchar(max) (default for an unbounded required string).
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

        // Model TRAINING run rows (Logs hub PR A) — one per training run. WRITTEN BY PYTHON later;
        // .NET owns the SCHEMA only here. Net-new, additive (no seed, no backfill). Version is the
        // business key (unique). MAE columns get explicit precision (house decimal discipline);
        // CreatedUtc is DB-defaulted so a Python INSERT that omits it still stamps a creation instant.
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

        // User ACTIVITY rows (Logs hub PR A) — one per security-relevant account event. WRITTEN by
        // the .NET side (IUserActivityAudit) at the five auth/user-management call sites; net-new,
        // additive (no seed, no backfill). EventType is enum-as-int (pinned). Only UsernameAttempted
        // (failed logins) + Details (short code-authored note) are free text, both length-capped; no
        // password/token/body is ever stored.
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

        // SYSTEM ERROR rows (Logs hub PR A / Phase 3) — one per unhandled 500. WRITTEN by the .NET side
        // (ISystemErrorLog from GlobalExceptionMiddleware) with a fire-safe, self-scoping writer; net-new,
        // additive (no seed, no backfill). OccurredUtc is DB-defaulted so any write path stamps an
        // instant. StackTrace is nvarchar(max) (the factory still hard-caps to 8000). Only exception
        // type/message/stack + request method/path (path only) + trace id are stored — no request
        // field (query string/header/body) is captured directly, but Message/StackTrace hold the
        // verbatim (length-capped) exception text; see the SystemError entity's PRIVACY note.
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
            },
            // ── R2 Step 6.2 — 6 additional HARTI bulletin markets ─────────────────────────
            // Added in lockstep with the 6.1 parser widening (10 real markets). Classification
            // is owner-verified best-evidence (web): Meegoda / Nuwara Eliya / Veyangoda are
            // formally-designated Dedicated Economic Centres (MarketType.DEC); Kandy /
            // Norochchole / Bandarawela are municipal/assembly wholesale markets
            // (MarketType.Wholesale, "(HARTI wholesale)" suffix like Pettah). Norochchole's
            // classification is the least certain of the three and is reclassifiable if
            // stronger evidence surfaces.
            //
            // IsEconomicCenter is deliberately NOT set here: per the R2 Step 3.1 convention
            // only MKT00000001 (Dambulla) carries IsEconomicCenter=1 today — Keppetipola /
            // Thambuttegama are MarketType.DEC yet IsEconomicCenter=0. MarketType classifies
            // the market *kind*; IsEconomicCenter=1 flags the single feature-reference DEC.
            // These 3 new DEC rows therefore keep the column default (false), matching the
            // existing seeded DEC rows. (Column defaultValue=false from migration
            // 20260706161839_AddIsEconomicCenterToMarket.)
            new
            {
                Id = Guid.Parse("b2a20001-0000-0000-0000-000000000007"),
                MarketCode = "MKT00000007",
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
                // Best-evidence classification: municipal/assembly wholesale market, not a
                // formally-designated DEC (least certain of the three — reclassifiable).
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
                Name = "Veyangoda Dedicated Economic Centre",
                District = (string?)"Gampaha",
                MarketType = MarketType.DEC,
                IsActive = true,
                CreatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 07, 07, 0, 0, 0, DateTimeKind.Utc)
            }
        );
    }

    // ─────────────────────────────────────────────────────────────────────────────────────
    // CROP CATEGORIES — reference dimension, manual update path (NO ingestion / CQRS / endpoint).
    //
    // Fixed lowercase GUIDs + a FIXED CreatedAt keep the seed deterministic and idempotent
    // across migrations (a UtcNow here would churn the diff every build). Mirrors the HARTI
    // bulletin grouping: top-level Vegetable / Fruit, plus Up-country / Low-country Vegetable
    // sub-categories whose ParentId points at Vegetable.
    //
    // NEVER HasData on Crop rows — crops are auto-provisioned at runtime with per-DB GUIDs.
    // Assigning categories onto the existing crops is a separate name-keyed backfill (subtask 1.2),
    // not seeded here.
    //
    // TO ADD A CATEGORY: add a seed row with a new fixed lowercase GUID + a unique Code, add a
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

    // ─────────────────────────────────────────────────────────────────────────────────────
    // FESTIVAL CALENDAR — manual annual update path (NO ingestion service / CQRS / endpoint).
    //
    // This is yearly-static reference data seeded via HasData with FIXED GUIDs and a FIXED
    // CreatedAtUtc (a UtcNow here would churn the migrations diff every build). The DB table is
    // the SINGLE SOURCE OF TRUTH — the Python feature layer reads it via load_festivals(); do
    // NOT add a static festival-days twin.
    //
    // TO UPDATE (annual gazette check, ~November each year — ClickUp task 86caj358h):
    //   1. Get the next year's holiday dates from the Department of Government Printing annual
    //      holiday gazette (https://www.documents.gov.lk/).
    //   2. Flip that year's AVURUDU / THAI_PONGAL rows from IsProvisional=true to false and set
    //      the real gazette Source citation; correct the Date if the gazette differs from the
    //      provisional estimate.
    //   3. Extend the seed forward by one year (add new provisional rows) so the seed always
    //      covers training-history-start (2015) .. current+N and never zeroes the feature for
    //      historical training rows (the silent-bug trap: a forward-only seed leaves ~95% of
    //      training rows with no festival signal while CV still looks fine).
    //   4. Add a migration and apply it.
    //
    // NOT SEEDED (intentionally): EID_UL_FITR / EID_UL_ADHA / DEEPAVALI. Eid dates require ACJU
    // moon-sighting verification and Deepavali requires per-year lunar verification — neither is
    // verifiable offline. Add them as seed rows (same shape) once dates are gazette-confirmed.
    //
    // Seed span: 2015 (training history starts 2015-06-22) .. 2030 inclusive.
    //   AVURUDU     — Sinhala & Tamil New Year, the Apr 13 (eve) + Apr 14 (day) PAIR each year.
    //                 The lead-up window anchors on the Apr 13 row (LeadUpDays=14); the paired
    //                 Apr 14 row carries LeadUpDays=0 so the demand window is not double-counted.
    //                 2015–2026 confirmed (IsProvisional=false); 2027–2030 provisional.
    //   CHRISTMAS   — Dec 25 each year, fixed date, IsProvisional=false for all (zero risk).
    //   THAI_PONGAL — Jan 14 each year (occasionally Jan 15). Folded in deliberately: the old
    //                 hardcoded Python _is_festival() covered Pongal and dropping it would lose
    //                 signal. Marked provisional where the gazette date could not be cited here.
    private static void SeedFestivalCalendar(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FestivalCalendarEntry>().HasData(GetFestivalCalendarSeed());
    }

    // Deterministic, wall-clock-free seed rows. Exposed so tests assert on the exact rows that
    // land in HasData (no EF-InMemory / DB round-trip needed, and one source of truth for both).
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

            // ── AVURUDU (Sinhala & Tamil New Year): Apr 13 eve + Apr 14 day, every year. ──
            // Apr 13 anchors the lead-up window (LeadUpDays=14).
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

            // ── THAI_PONGAL: Jan 14 (folded in; provisional where not gazette-cited here). ──
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

            // ── CHRISTMAS: Dec 25, fixed date — confirmed for all years (zero risk). ──
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

    // Deterministic fixed GUID per (festival tag, year): c3f30001-0000-0000-{tag}-00000000{yyyy}.
    // Constant-folded at model build → EF snapshots literal GUIDs; stable across migrations.
    // NOTE: {year:0000} embeds DECIMAL year digits in a hex GUID group — valid only because
    // digits 0-9 are hex-safe by construction. Do not reuse this pattern with non-digit values.
    private static Guid FestivalId(int tag, int year)
        => Guid.Parse($"c3f30001-0000-0000-{tag:x4}-00000000{year:0000}");
}
