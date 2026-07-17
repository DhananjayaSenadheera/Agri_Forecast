using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHartiFruitAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Seed: 4 HARTI FRUIT commodity aliases -> canonical Crop (R2 fruits step 1) ---
            // Extends the vegetable-only HARTI alias set (20260702190530 + 20260707093230) with
            // the first 4 fruits: the daily bulletin's fruit subsection (Banana group + Papaya)
            // rows that are priced per-kg and map 1:1 onto an existing per-kg DB crop. Alias
            // strings are byte-for-byte the canonical labels parser.py's _TARGET_FRUITS_PER_KG
            // emits ("Ambul", "Kolikuttu", "Seeni", "Papaya") -- NOT the raw per-era PDF cell text
            // (e.g. "Ambul(Rs/Kg)"), matching the existing HARTI alias convention of keying on the
            // parser's post-consolidation canonical label, not the raw cell.
            //
            // Deliberately OUT OF SCOPE (per-fruit units, no per-kg DB crop to map to -- see
            // parser.py _TARGET_FRUITS_PER_KG / _TARGET_FRUITS_PER_FRUIT_SKIP docstrings):
            // Anamalu, Passion Fruits, Pineapple, Mango, Woodapple, Avocado, Orange.
            //
            // WHY migrationBuilder.Sql AND NOT HasData: same reason as 20260702190530 /
            // 20260707093230 -- canonical CropIds are RUNTIME-GENERATED (per-DB GUIDs), never
            // fixed seeds, so we back-fill by JOINING to Crops on the stable, portable CropCode
            // (D-DF4), never a GUID or the drift-prone Name. Idempotent: INSERT ... SELECT with a
            // NOT EXISTS(Alias, Source='HARTI') guard; INNER JOIN so a missing/renamed crop yields
            // zero rows for that alias, never an FK violation.
            //
            // Alias (HARTI canonical label) -> CropCode -> (DB Crop.Name):
            //   Ambul     -> FRT000003 (Banana - Abul)
            //   Kolikuttu -> FRT000005 (Banana - Kolikuttu)
            //   Seeni     -> FRT000006 (Banana - Sini)
            //   Papaya    -> FRT000018 (Papaya)
            migrationBuilder.Sql(@"
INSERT INTO [CommodityAliases] ([Id], [Alias], [CropId], [Source], [Language], [IsActive], [CreatedAtUtc])
SELECT NEWID(), v.[Alias], c.[Id], 'HARTI', 'en', 1, SYSUTCDATETIME()
FROM (VALUES
        ('Ambul',     'FRT000003'),
        ('Kolikuttu', 'FRT000005'),
        ('Seeni',     'FRT000006'),
        ('Papaya',    'FRT000018')
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
            // Reverse ONLY the 4 aliases this migration added -- never any other Source='HARTI'
            // row. Keyed on the exact (Alias, Source='HARTI') pairs.
            migrationBuilder.Sql(@"
DELETE FROM [CommodityAliases]
WHERE [Source] = 'HARTI'
  AND [Alias] IN ('Ambul', 'Kolikuttu', 'Seeni', 'Papaya');
");
        }
    }
}
