using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCommodityAliasAndUnitQuarantine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUnitConfirmed",
                table: "PriceObservations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitConversionFactor",
                table: "PriceObservations",
                type: "decimal(10,4)",
                precision: 10,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitRaw",
                table: "PriceObservations",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CommodityAliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CropId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Language = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommodityAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommodityAliases_Crops_CropId",
                        column: x => x.CropId,
                        principalTable: "Crops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommodityAliases_AliasSourceActive",
                table: "CommodityAliases",
                columns: new[] { "Alias", "Source", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_CommodityAliases_CropId",
                table: "CommodityAliases",
                column: "CropId");

            migrationBuilder.CreateIndex(
                name: "UX_CommodityAliases_AliasGlobal",
                table: "CommodityAliases",
                column: "Alias",
                unique: true,
                filter: "[Source] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CommodityAliases_AliasSource",
                table: "CommodityAliases",
                columns: new[] { "Alias", "Source" },
                unique: true,
                filter: "[Source] IS NOT NULL");

            // --- Seed: HARTI commodity labels -> canonical Crop (R1.1 P1) --------------------
            // Seeds one CommodityAlias per (HARTI label -> Crop) for the 6 R1.1 target crops,
            // scoped Source='HARTI', Language='en', IsActive=1.
            //
            // WHY migrationBuilder.Sql AND NOT HasData:
            //   The canonical CropIds are RUNTIME-GENERATED (Crop.CreateFromExternalSource /
            //   CreateForManualEntry mint Guid.NewGuid()); they are NOT seeded with fixed GUIDs.
            //   HasData requires compile-time-fixed key values, and any GUID we hardcoded here
            //   would NOT match the live Crops rows — the FK would point at nothing and the
            //   alias would never resolve. So we back-fill by JOINING on Crops.Name (the stable,
            //   human-curated business key), exactly mirroring the ECOMAP name/PK-keyed back-fill
            //   in the previous migration.
            //
            // Label mismatches this table exists to fix (PRD risk R5 High/High) — verified against
            // the live Crops table and src/AgriForecast.ML/agriforecast_ml/harti/parser.py
            // (_TARGET_CROPS canonical values: Beans, Ladies Fingers, Capsicum, Bitter Gourd,
            //  Luffa, Snake Gourd):
            //   'Ladies Fingers' (HARTI) -> 'Lady's Fingers' (Crop)  — spelling/possessive drift
            //   'Luffa'          (HARTI) -> 'Ridge Gourd'    (Crop)  — Luffa acutangula = ridge gourd
            //   the other four match the Crop.Name verbatim.
            //
            // Idempotent & null-safe: INSERT ... SELECT with a NOT EXISTS guard on
            // (Alias, Source), and an INNER JOIN to Crops so a missing/renamed crop simply
            // yields zero rows for that alias rather than an FK violation (self-healing — a later
            // re-run once the crop exists will fill it in). NEWID() for each alias PK is fine
            // (back-fill rows, not a deterministic seed). CreatedAtUtc = SYSUTCDATETIME().
            //
            // Case-insensitivity: the JOIN and the NOT EXISTS both run under SQL Server's default
            // case-INSENSITIVE collation — desirable here (see CommodityAlias / DbContext notes).
            migrationBuilder.Sql(@"
INSERT INTO [CommodityAliases] ([Id], [Alias], [CropId], [Source], [Language], [IsActive], [CreatedAtUtc])
SELECT NEWID(), v.[Alias], c.[Id], 'HARTI', 'en', 1, SYSUTCDATETIME()
FROM (VALUES
        ('Beans',          'Beans'),
        ('Ladies Fingers', 'Lady''s Fingers'),
        ('Capsicum',       'Capsicum'),
        ('Bitter Gourd',   'Bitter Gourd'),
        ('Luffa',          'Ridge Gourd'),
        ('Snake Gourd',    'Snake Gourd')
     ) AS v([Alias], [CropName])
INNER JOIN [Crops] c ON c.[Name] = v.[CropName]
WHERE NOT EXISTS (
        SELECT 1 FROM [CommodityAliases] a
        WHERE a.[Alias] = v.[Alias] AND a.[Source] = 'HARTI'
      );
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the HARTI seed explicitly before the table drop. DropTable would remove
            // these rows anyway, but deleting exactly the 6 seeded (Alias, Source='HARTI') pairs
            // keeps the reversal intentional and correct regardless of drop ordering, and never
            // touches any user-added aliases that don't match this list.
            migrationBuilder.Sql(@"
DELETE FROM [CommodityAliases]
WHERE [Source] = 'HARTI'
  AND [Alias] IN ('Beans', 'Ladies Fingers', 'Capsicum', 'Bitter Gourd', 'Luffa', 'Snake Gourd');
");

            migrationBuilder.DropTable(
                name: "CommodityAliases");

            migrationBuilder.DropColumn(
                name: "IsUnitConfirmed",
                table: "PriceObservations");

            migrationBuilder.DropColumn(
                name: "UnitConversionFactor",
                table: "PriceObservations");

            migrationBuilder.DropColumn(
                name: "UnitRaw",
                table: "PriceObservations");
        }
    }
}
