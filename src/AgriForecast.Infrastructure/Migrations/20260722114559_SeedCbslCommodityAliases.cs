using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedCbslCommodityAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Seed: 15 Source='CBSL' commodity aliases -> canonical Crop (CBSL parser follow-up #1) ---
            // The CBSL Daily Price Report ingestion (20260722, capture-only) stores rows keyed on the
            // report's own labels; until these aliases exist every CBSL row lands with CropId NULL
            // (the resolver never guesses). heal_price_observation_crops() back-fills the already-
            // captured NULL rows automatically on the next ingest pass once these land.
            //
            // Alias strings are byte-for-byte the labels the parser emits from the live PDFs —
            // including 'Red Onion (lmp)' (lowercase L), which is how the report's kerning renders
            // "(Imp)"; the correctly-spelled 'Red Onion (Imp)' twin is seeded alongside it so a
            // future font/layout fix on CBSL's side keeps resolving. Alias lookup is case-insensitive
            // (DB CI collation + resolver _normalise_key), so 'Snake gourd' matches as stored.
            //
            // Mapping precedent (existing HARTI/DEC aliases):
            //   Banana (Sour) -> Banana - Abul  ("ambul" = sour banana; HARTI 'Ambul' precedent)
            //   Papaw         -> Papaya         (British/Lankan spelling; HARTI 'Papaya')
            //   *(Imp)/(Local) variants follow HARTI 'Potato (Imported)' / 'Big Onion Local' style.
            //
            // Deliberately NOT seeded (unresolved-by-design, CropId stays NULL + WARN):
            //   Carrot            — DB has only variety crops (Carrot - Jaffna / Nuwaraeliya Carrot);
            //   Potato (Local)    — DB has only regional crops (Nuwaraeliya / Jaffna / Walimada);
            //   Pumpkin           — DB has Pumpkin - Big vs Pumpkin - Malashian;
            //   Dried Chilli (Imp)— imported product; mapping onto the locally-grown 'Dry Chillies'
            //                       crop would blend two different market goods into one series;
            //   Katta (Imp), Sprat (Imp), Red Dhal, Sugar (White) — not crops (fish/lentil/processed).
            // Guessing any of these would silently corrupt a crop's price series — worse than a
            // visible gap (see canonical.py never-guess contract). Owner may adjudicate later.
            //
            // WHY migrationBuilder.Sql AND NOT HasData: same reason as 20260702190530 / 20260717004557
            // — canonical CropIds are RUNTIME-GENERATED (per-DB GUIDs), never fixed seeds, so we
            // back-fill by JOINING to Crops on the stable, portable CropCode. Idempotent:
            // INSERT ... SELECT with a NOT EXISTS(Alias, Source='CBSL') guard; INNER JOIN so a
            // missing/renamed crop yields zero rows for that alias, never an FK violation.
            //
            // Alias (CBSL report label) -> CropCode -> (DB Crop.Name):
            //   Beans             -> VEG000003 (Beans)
            //   Brinjal           -> VEG000012 (Brinjal)
            //   Cabbage           -> VEG000014 (Cabbage)
            //   Tomato            -> VEG000065 (Tomato)
            //   Green Chilli      -> VEG000031 (Green Chili)
            //   Snake gourd       -> VEG000059 (Snake Gourd)
            //   Lime              -> FRT000013 (Lime)
            //   Papaw             -> FRT000018 (Papaya)
            //   Pineapple         -> FRT000021 (Pineapple)
            //   Banana (Sour)     -> FRT000003 (Banana - Abul)
            //   Potato (Imp)      -> VEG000047 (Potatoes - Import)
            //   Big Onion (Imp)   -> VEG000008 (Big Onion Import)
            //   Red Onion (lmp)   -> VEG000055 (Red Onion Import)   [as-captured kerning artifact]
            //   Red Onion (Imp)   -> VEG000055 (Red Onion Import)   [future-proof correct spelling]
            //   Red Onion (Local) -> VEG000056 (Red Onion- Lanka)
            migrationBuilder.Sql(@"
INSERT INTO [CommodityAliases] ([Id], [Alias], [CropId], [Source], [Language], [IsActive], [CreatedAtUtc])
SELECT NEWID(), v.[Alias], c.[Id], 'CBSL', 'en', 1, SYSUTCDATETIME()
FROM (VALUES
        ('Beans',             'VEG000003'),
        ('Brinjal',           'VEG000012'),
        ('Cabbage',           'VEG000014'),
        ('Tomato',            'VEG000065'),
        ('Green Chilli',      'VEG000031'),
        ('Snake gourd',       'VEG000059'),
        ('Lime',              'FRT000013'),
        ('Papaw',             'FRT000018'),
        ('Pineapple',         'FRT000021'),
        ('Banana (Sour)',     'FRT000003'),
        ('Potato (Imp)',      'VEG000047'),
        ('Big Onion (Imp)',   'VEG000008'),
        ('Red Onion (lmp)',   'VEG000055'),
        ('Red Onion (Imp)',   'VEG000055'),
        ('Red Onion (Local)', 'VEG000056')
     ) AS v([Alias], [CropCode])
INNER JOIN [Crops] c ON c.[CropCode] = v.[CropCode]
WHERE NOT EXISTS (
        SELECT 1 FROM [CommodityAliases] a
        WHERE a.[Alias] = v.[Alias] AND a.[Source] = 'CBSL'
      );
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse ONLY the aliases this migration added -- never any other Source='CBSL' row.
            // Keyed on the exact (Alias, Source='CBSL') pairs.
            migrationBuilder.Sql(@"
DELETE FROM [CommodityAliases]
WHERE [Source] = 'CBSL'
  AND [Alias] IN ('Beans', 'Brinjal', 'Cabbage', 'Tomato', 'Green Chilli', 'Snake gourd',
                  'Lime', 'Papaw', 'Pineapple', 'Banana (Sour)', 'Potato (Imp)',
                  'Big Onion (Imp)', 'Red Onion (lmp)', 'Red Onion (Imp)', 'Red Onion (Local)');
");
        }
    }
}
