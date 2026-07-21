using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixGarlicAliasMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Repair: DEC product 47 is 'Garlic', not 'Big Onion' (chip task_45d101d5) ----------
            // CONFIRMED DEFECT: the SeedDambullaCommodityAliases migration (20260708222951, line
            // ('47','VEG000007')) mapped Dambulla-DEC feed product id 47 to Big Onion (VEG000007).
            // The feed product 47 is actually GARLIC — proven conclusively by our OWN data: every
            // MarketPrices row Source='DAMBULLA_DEC' AND ExternalProductId=47 carries the feed's
            // ExternalProductName='Garlic' (392 rows at repair time, span 2025-05-05..2026-07-10).
            // So every DEC ext-47 price is a Garlic price wrongly attributed to Big Onion.
            //
            // FIX (all in ONE transaction — EF wraps each migration; fail-closed, no partial repair):
            //   a. Create a 'Garlic' Crop (next VEG code from the DefaultSettings counter, mirroring
            //      CodeSettings.GetCropCode) + bump the counter so future registrations never collide.
            //   b. Create a PENDING agronomy profile (IsVerified=0, GrowthPeriodDays NULL) — this keeps
            //      Garlic EXCLUDED from forecasting via the IsVerified-strict gate (correct fail-closed
            //      behaviour: we have no DOA-verified agronomy for Garlic yet).
            //   c. Repoint the alias (Alias='47', Source='DAMBULLA_DEC') off Big Onion onto Garlic.
            //   d. Repoint every mislabelled MarketPrices ext-47 row off Big Onion onto Garlic.
            //
            // KEYING (D-DF4): everything resolves through the stable display-only CropCode / the
            // (Alias, Source) and (Source, ExternalProductId) natural keys — NEVER a hard-coded GUID
            // (GUIDs are per-DB runtime-minted). NEWID() for new PKs (repair rows, not fixed seeds).
            //
            // FK NOTE: this migration only INSERTs a Crop + its 1:1 CropAgronomyProfile and UPDATEs
            // CropId pointers — it deletes NOTHING, so none of the Crop-referencing cascade/set-null
            // FKs (CropPrices=Cascade, PriceObservations=SetNull, CropAgronomyProfiles=Restrict) can
            // fire. After the alias repoint, DEC ingestion resolves product 47 -> Garlic, so the
            // auto-provision path creates no duplicate crop. No code change is required (the alias
            // route governs DEC resolution).
            migrationBuilder.Sql(@"
-- ---- Fail-closed guards (run BEFORE any write) -------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM [CommodityAliases]
               WHERE [Alias] = '47' AND [Source] = 'DAMBULLA_DEC')
    THROW 50000, 'FixGarlicAliasMapping: CommodityAlias (Alias=47, Source=DAMBULLA_DEC) is missing — aborting.', 1;

IF EXISTS (SELECT 1 FROM [Crops] WHERE [Name] = 'Garlic')
    THROW 50000, 'FixGarlicAliasMapping: a crop named Garlic already exists — aborting (no double-provision).', 1;

DECLARE @vegCatId UNIQUEIDENTIFIER = (SELECT [Id] FROM [CropCategories] WHERE [Code] = 'VEG');
IF @vegCatId IS NULL
    THROW 50000, 'FixGarlicAliasMapping: Vegetable category (Code=VEG) is missing — aborting.', 1;

DECLARE @bigOnionId UNIQUEIDENTIFIER = (SELECT [Id] FROM [Crops] WHERE [CropCode] = 'VEG000007');
IF @bigOnionId IS NULL
    THROW 50000, 'FixGarlicAliasMapping: Big Onion crop (VEG000007) is missing — aborting.', 1;

-- Read the single-row VEG code counter (mirrors CodeSettings.GetCropCode: prefix + zero-padded).
DECLARE @vegPrefix  NVARCHAR(10);
DECLARE @vegCounter INT;
DECLARE @vegPadding INT;
SELECT @vegPrefix = [Veg_Prefix], @vegCounter = [Veg_Code], @vegPadding = [Veg_Padding]
FROM [DefaultSettings] WHERE [Id] = 1;
IF @vegCounter IS NULL
    THROW 50000, 'FixGarlicAliasMapping: DefaultSettings Veg_Code counter is missing — aborting.', 1;

DECLARE @vegCode NVARCHAR(50) =
    @vegPrefix + RIGHT(REPLICATE('0', @vegPadding) + CAST(@vegCounter AS NVARCHAR(20)), @vegPadding);

-- The pre-repair damage set must be non-empty, else something is wrong — refuse a silent no-op.
DECLARE @preCount INT = (SELECT COUNT(*) FROM [MarketPrices]
                         WHERE [Source] = 'DAMBULLA_DEC' AND [ExternalProductId] = 47);
IF @preCount = 0
    THROW 50000, 'FixGarlicAliasMapping: zero DAMBULLA_DEC ExternalProductId=47 MarketPrices rows to repoint — aborting.', 1;

-- ---- a. Insert the Garlic crop + bump the VEG counter ------------------------------------------
DECLARE @garlicId UNIQUEIDENTIFIER = NEWID();
INSERT INTO [Crops] ([Id], [CropCode], [Name], [Source], [CropCategoryId], [CreatedAt], [UpdatedAt])
VALUES (@garlicId, @vegCode, 'Garlic', 'DAMBULLA_DEC', @vegCatId, SYSUTCDATETIME(), SYSUTCDATETIME());

UPDATE [DefaultSettings] SET [Veg_Code] = [Veg_Code] + 1 WHERE [Id] = 1;

-- ---- b. Insert the PENDING agronomy profile (unverified => excluded from forecasting) ----------
INSERT INTO [CropAgronomyProfiles]
    ([Id], [CropId], [GrowthPeriodDays], [HarvestWindowDays],
     [YalaPlantingStartMonth], [YalaPlantingEndMonth], [MahaPlantingStartMonth], [MahaPlantingEndMonth],
     [IsPerennial], [DataSource], [VerifiedOn], [IsVerified], [CreatedAt], [UpdatedAt])
VALUES
    (NEWID(), @garlicId, NULL, NULL,
     NULL, NULL, NULL, NULL,
     0, 'pending-research (chip task_45d101d5)', NULL, 0, SYSUTCDATETIME(), SYSUTCDATETIME());

-- ---- c. Repoint the alias off Big Onion onto Garlic (must touch EXACTLY 1 row) -----------------
UPDATE [CommodityAliases] SET [CropId] = @garlicId
WHERE [Alias] = '47' AND [Source] = 'DAMBULLA_DEC';
IF @@ROWCOUNT <> 1
    THROW 50000, 'FixGarlicAliasMapping: expected exactly 1 alias (47, DAMBULLA_DEC) to repoint.', 1;

-- ---- d. Repoint the mislabelled MarketPrices ext-47 rows off Big Onion onto Garlic -------------
-- Keyed on the (Source, ExternalProductId) natural key so it catches ext-47 rows COMMITTED
-- before this statement runs. NOT race-proof: MarketPriceIngestionService caches the alias
-- route once at run start, so an ingestion run in flight across this migration can commit
-- stale Big Onion ext-47 rows afterwards (and BackfillCropIdAsync only heals NULL CropId) —
-- apply outside the ingestion window and post-verify 0 ext-47 rows remain on Big Onion.
-- >=0 sanity log, not a hard pin (the pre-count>0 guard already fired).
UPDATE [MarketPrices] SET [CropId] = @garlicId
WHERE [Source] = 'DAMBULLA_DEC' AND [ExternalProductId] = 47;
PRINT CONCAT('FixGarlicAliasMapping: repointed ', @@ROWCOUNT,
             ' MarketPrices ext-47 rows Big Onion -> Garlic (', @vegCode, ').');
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberate one-way no-op (precedent: 20260708205157 / 20260716072337). This repair
            // creates a new Garlic crop, advances the assign-once VEG code counter (never reissued),
            // and repoints alias + price history off a wrong mapping onto the correct one. Rolling
            // that back would silently re-attribute Garlic prices to Big Onion again — the very
            // defect being fixed — so there is no safe automated Down. Reverting means restoring
            // from a backup, not running this Down.
        }
    }
}
