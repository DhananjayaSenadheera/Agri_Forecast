using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Step6WidenHartiMarketsAndAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "DefaultSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "Mkt_Code",
                value: 13);

            migrationBuilder.InsertData(
                table: "Markets",
                columns: new[] { "Id", "CreatedAt", "District", "IsActive", "MarketCode", "MarketType", "Name", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("b2a20001-0000-0000-0000-000000000007"), new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc), "Kandy", true, "MKT00000007", 0, "Kandy (HARTI wholesale)", new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("b2a20001-0000-0000-0000-000000000008"), new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc), "Colombo", true, "MKT00000008", 2, "Meegoda Dedicated Economic Centre", new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("b2a20001-0000-0000-0000-000000000009"), new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc), "Puttalam", true, "MKT00000009", 0, "Norochchole (HARTI wholesale)", new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("b2a20001-0000-0000-0000-000000000010"), new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc), "Nuwara Eliya", true, "MKT00000010", 2, "Nuwara Eliya Dedicated Economic Centre", new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("b2a20001-0000-0000-0000-000000000011"), new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc), "Badulla", true, "MKT00000011", 0, "Bandarawela (HARTI wholesale)", new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("b2a20001-0000-0000-0000-000000000012"), new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc), "Gampaha", true, "MKT00000012", 2, "Veyangoda Dedicated Economic Centre", new DateTime(2026, 7, 7, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            // --- Seed: 18 NEW HARTI commodity aliases -> canonical Crop (R2 Step 6.2) ---------
            // Extends the 6 aliases seeded by 20260702190530 (Beans, Ladies Fingers, Capsicum,
            // Bitter Gourd, Luffa, Snake Gourd) up to the full 24 canonical HARTI labels the
            // widened parser now emits (= the KEYS of loader.py `_HARTI_TO_DB_NAME`, which is
            // the single source of truth — these Alias strings are byte-for-byte those keys).
            //
            // WHY migrationBuilder.Sql AND NOT HasData: same reason as 20260702190530 — canonical
            // CropIds are RUNTIME-GENERATED (per-DB GUIDs), never fixed seeds, so we back-fill by
            // JOINING to Crops. Difference vs 20260702190530: we JOIN on Crops.CropCode, NOT
            // Crops.Name (post-Step-4 portability rule D-DF4 — migration keys = the stable
            // display-only business code VEG######/FRT######, never GUID/Name). Idempotent:
            // INSERT ... SELECT with a NOT EXISTS(Alias, Source='HARTI') guard; INNER JOIN so a
            // missing/renamed crop yields zero rows for that alias, never an FK violation.
            //
            // Alias (HARTI label)      -> CropCode -> (DB Crop.Name, per loader.py):
            //   Green Chillies -> VEG000031 (Green Chili)        Tomato        -> VEG000065 (Tomato)
            //   Leeks          -> VEG000036 (Leeks)              Knolkhol      -> VEG000042 (Nnolkhol)
            //   Raddish        -> VEG000053 (Raddish)            Cucumber      -> VEG000022 (Cucumber)
            //   Drumstick      -> VEG000024 (Drumsticks)         Long Beans    -> VEG000070 (Yard - Long Beans)
            //   Ash Plantains  -> VEG000001 (Ash Plantain)       Lime          -> FRT000013 (Lime)
            //   Sweet Potato   -> VEG000061 (Sweet Potato)       Manioc        -> VEG000039 (Manioc)
            //   Brinjals       -> VEG000012 (Brinjal)            Potato (Imported)    -> VEG000047 (Potatoes - Import)
            //   Potato (Welimada)    -> VEG000050 (Potatoes - Walimada)
            //   Potato (Nuwaraeliya) -> VEG000049 (Potatoes - Nuwaraeliya)
            //   Big Onion Imported   -> VEG000008 (Big Onion Import)
            //   Big Onion Local      -> VEG000009 (Big Onion Lanka)
            migrationBuilder.Sql(@"
INSERT INTO [CommodityAliases] ([Id], [Alias], [CropId], [Source], [Language], [IsActive], [CreatedAtUtc])
SELECT NEWID(), v.[Alias], c.[Id], 'HARTI', 'en', 1, SYSUTCDATETIME()
FROM (VALUES
        ('Green Chillies',       'VEG000031'),
        ('Tomato',               'VEG000065'),
        ('Leeks',                'VEG000036'),
        ('Knolkhol',             'VEG000042'),
        ('Raddish',              'VEG000053'),
        ('Cucumber',             'VEG000022'),
        ('Drumstick',            'VEG000024'),
        ('Long Beans',           'VEG000070'),
        ('Ash Plantains',        'VEG000001'),
        ('Lime',                 'FRT000013'),
        ('Sweet Potato',         'VEG000061'),
        ('Manioc',               'VEG000039'),
        ('Brinjals',             'VEG000012'),
        ('Potato (Imported)',    'VEG000047'),
        ('Potato (Welimada)',    'VEG000050'),
        ('Potato (Nuwaraeliya)', 'VEG000049'),
        ('Big Onion Imported',   'VEG000008'),
        ('Big Onion Local',      'VEG000009')
     ) AS v([Alias], [CropCode])
INNER JOIN [Crops] c ON c.[CropCode] = v.[CropCode]
WHERE NOT EXISTS (
        SELECT 1 FROM [CommodityAliases] a
        WHERE a.[Alias] = v.[Alias] AND a.[Source] = 'HARTI'
      );
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse ONLY the 18 aliases this migration added — never the 6 from
            // 20260702190530 nor any user-added Source='HARTI' rows. Keyed on the exact
            // (Alias, Source='HARTI') pairs.
            migrationBuilder.Sql(@"
DELETE FROM [CommodityAliases]
WHERE [Source] = 'HARTI'
  AND [Alias] IN (
        'Green Chillies', 'Tomato', 'Leeks', 'Knolkhol', 'Raddish', 'Cucumber',
        'Drumstick', 'Long Beans', 'Ash Plantains', 'Lime', 'Sweet Potato', 'Manioc',
        'Brinjals', 'Potato (Imported)', 'Potato (Welimada)', 'Potato (Nuwaraeliya)',
        'Big Onion Imported', 'Big Onion Local'
      );
");

            migrationBuilder.DeleteData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000009"));

            migrationBuilder.DeleteData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000010"));

            migrationBuilder.DeleteData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000011"));

            migrationBuilder.DeleteData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000012"));

            migrationBuilder.UpdateData(
                table: "DefaultSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "Mkt_Code",
                value: 7);
        }
    }
}
