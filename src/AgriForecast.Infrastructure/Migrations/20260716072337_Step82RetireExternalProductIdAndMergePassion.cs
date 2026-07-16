using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Step82RetireExternalProductIdAndMergePassion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // R2 Step 8.2 — two coupled changes, applied in one transaction:
            //   (A) MERGE the duplicate "Passion" crop: survivor FRT000019 keeps everything,
            //       retire FRT000020 is folded in and deleted. Owner-approved survivor choice.
            //   (B) DROP the legacy Crops.ExternalProductId column — the crop<->feed-product
            //       mapping is now OWNED solely by CommodityAliases (governing route promoted in
            //       MarketPriceIngestionService this same subtask).
            //
            // ALL ids are resolved by the stable display-only CropCode (D-DF4), NEVER a hard-coded
            // GUID (CropCodes portable across DBs; GUIDs are per-DB runtime-minted). The merge is
            // wrapped in `IF @retireId IS NOT NULL` so it is a clean no-op on a fresh DB where the
            // auto-provisioned Passion crops do not yet exist — the column drop still runs.
            //
            // ORDER MATTERS: MarketPrices.CropId has NO FK (orphans are silent), so its repoint
            // MUST happen before the crop delete. A fail-closed ZERO-remaining-references gate runs
            // immediately before the delete and THROWs rather than orphaning or cascading anything.
            migrationBuilder.Sql(@"
DECLARE @retireId   UNIQUEIDENTIFIER = (SELECT [Id] FROM [Crops] WHERE [CropCode] = 'FRT000020');
DECLARE @survivorId UNIQUEIDENTIFIER = (SELECT [Id] FROM [Crops] WHERE [CropCode] = 'FRT000019');

IF @retireId IS NOT NULL
BEGIN
    -- Survivor must exist if the retire crop does; otherwise the repoint targets NULL.
    IF @survivorId IS NULL
        THROW 50000, 'Step 8.2 Passion merge: retire crop FRT000020 present but survivor FRT000019 missing — aborting.', 1;

    -- 1. Repoint the Dambulla DEC alias '43' (Passion, retired product id) at the survivor.
    --    Must touch EXACTLY 1 row: the SeedDambullaCommodityAliases migration seeded one '43'
    --    row and the (Alias, Source) filtered unique index forbids a second.
    UPDATE [CommodityAliases]
    SET [CropId] = @survivorId
    WHERE [Alias] = '43' AND [Source] = 'DAMBULLA_DEC';

    IF @@ROWCOUNT <> 1
        THROW 50000, 'Step 8.2 Passion merge: expected exactly 1 CommodityAlias (Alias=43, Source=DAMBULLA_DEC) to repoint.', 1;

    -- 2. Repoint every MarketPrices row from the retired crop to the survivor. This is the
    --    mandatory FIRST data move (no FK on MarketPrices.CropId — orphans would be silent).
    --    Row count is a >=0 sanity log, not a hard pin (daily ingestion may have added rows).
    UPDATE [MarketPrices]
    SET [CropId] = @survivorId
    WHERE [CropId] = @retireId;
    DECLARE @repointed INT = @@ROWCOUNT;
    PRINT CONCAT('Step 8.2 Passion merge: repointed ', @repointed, ' MarketPrices rows FRT000020 -> FRT000019.');

    -- 3. Delete the retired crop's agronomy profile (1:1). CropAgronomyProfiles -> Crops is a
    --    Restrict FK, so this MUST precede the crop delete.
    DELETE FROM [CropAgronomyProfiles] WHERE [CropId] = @retireId;

    -- 4. Delete the retired crop's Python-owned CropFeatureDaily rows. This table is NOT in the
    --    EF model, so it is dropped via raw SQL guarded by an OBJECT_ID existence check (absent on
    --    a DB the ML feature build has never run against).
    IF OBJECT_ID('CropFeatureDaily', 'U') IS NOT NULL
        DELETE FROM [CropFeatureDaily] WHERE [CropId] = @retireId;

    -- 5. FAIL-CLOSED GATE: refuse to delete the crop while ANY row still references it. Must cover
    --    every Crop-referencing FK, ESPECIALLY the two whose delete behavior is silent — CropPrices
    --    (Cascade: rows would vanish) and PriceObservations (SetNull: link would silently orphan) —
    --    since those are exactly the ones the DB engine will never throw on.
    IF EXISTS (SELECT 1 FROM [CommodityAliases] WHERE [CropId] = @retireId)
        THROW 50000, 'Step 8.2 Passion merge: FRT000020 still has CommodityAliases rows; aborting crop delete.', 1;
    IF EXISTS (SELECT 1 FROM [MarketPrices] WHERE [CropId] = @retireId)
        THROW 50000, 'Step 8.2 Passion merge: FRT000020 still has MarketPrices rows; aborting crop delete.', 1;
    IF EXISTS (SELECT 1 FROM [CropPrices] WHERE [CropId] = @retireId)
        THROW 50000, 'Step 8.2 Passion merge: FRT000020 still has CropPrices rows (Cascade would silently delete them); aborting crop delete.', 1;
    IF EXISTS (SELECT 1 FROM [PriceObservations] WHERE [CropId] = @retireId)
        THROW 50000, 'Step 8.2 Passion merge: FRT000020 still has PriceObservations rows (SetNull would silently orphan them); aborting crop delete.', 1;

    -- 6. Delete the retired duplicate crop. Its CropCode (FRT000020) is assign-once and is NEVER
    --    reissued (the DefaultSetting counter only ever moves forward).
    DELETE FROM [Crops] WHERE [CropCode] = 'FRT000020';
END
");

            // (B) Drop the retired column. Runs unconditionally (fresh DBs never had data in it).
            migrationBuilder.DropColumn(
                name: "ExternalProductId",
                table: "Crops");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberate one-way no-op (precedent: 20260708205157). The Passion merge folds two
            // crops into one and deletes the retired row + its feature history — that data-level
            // collapse is not row-precisely reversible, and re-adding an empty ExternalProductId
            // column would not restore the retired crop, its mapping, or its price history. Rolling
            // back to before Step 8.2 means restoring from a backup, not running this Down.
        }
    }
}
