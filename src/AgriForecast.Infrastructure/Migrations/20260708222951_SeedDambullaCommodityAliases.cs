using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedDambullaCommodityAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Seed: Dambulla DEC product -> canonical Crop aliases (R2 Step 8.1) -----------
            // Purpose: begin retiring the legacy Crops.ExternalProductId matching route
            // (MarketPriceIngestionService) in favour of the CommodityAliases route. This seeds
            // one alias per (DEC product -> Crop) pairing that currently lives in
            // Crops.ExternalProductId (Source='DAMBULLA_DEC'). Step 8.2 (later, only after the
            // parallel-run proves shadow-diff = 0) drops the Crops.ExternalProductId column.
            //
            // DEC KEYING DECISION — Alias = the ProductId, STRINGIFIED; Source='DAMBULLA_DEC':
            //   The Dambulla feed guarantees a STABLE integer ProductId per commodity; the human
            //   product NAME drifts (typos, renames, "Mango- Other" vs "Mango Other"). The legacy
            //   route already matched on Crops.ExternalProductId (= that same int), so the stable
            //   identity to preserve is the ProductId, NOT the name. CommodityAlias.Alias is an
            //   nvarchar(200), so the int is stored as its decimal string ('1', '11', '101').
            //   This needs NO schema change: the existing filtered unique index
            //   (Alias, Source) WHERE Source IS NOT NULL enforces "one crop per DEC product",
            //   exactly the invariant Crops.ExternalProductId held. HARTI aliases (name-keyed,
            //   Source='HARTI') and DEC aliases (id-keyed, Source='DAMBULLA_DEC') coexist because
            //   the Source discriminator scopes each set — an id like '11' never collides with a
            //   HARTI label. Language left NULL: the Alias is an opaque id, not natural-language text.
            //
            // WHY migrationBuilder.Sql AND NOT HasData: same rule as 20260702190530 /
            //   20260707093230 — canonical CropIds are RUNTIME-GENERATED per-DB GUIDs, never fixed
            //   seeds, so we back-fill by JOINING to Crops. Keyed on Crops.CropCode (post-Step-4
            //   portability rule D-DF4: migration keys = the stable display-only VEG######/FRT######
            //   code, NEVER a GUID or the drift-prone Name). The (ProductId, CropCode) pairs below
            //   were derived from a live query of Crops WHERE Source='DAMBULLA_DEC' (96 rows, no
            //   NULL/duplicate CropCodes, no duplicate ProductIds), but the migration itself is
            //   deterministic and re-runnable on a fresh DB because it keys on CropCode, not GUIDs.
            //
            // Idempotent & null-safe: INSERT ... SELECT with a NOT EXISTS guard on
            // (Alias, Source='DAMBULLA_DEC'), and an INNER JOIN to Crops so a missing/renamed crop
            // simply yields zero rows for that alias rather than an FK violation (self-healing — a
            // later re-run once the crop exists fills it in). NEWID() per PK (back-fill rows, not a
            // deterministic seed). CreatedAtUtc = SYSUTCDATETIME(). Runs under SQL Server's default
            // case-INSENSITIVE collation (irrelevant for numeric-string aliases, consistent with
            // the HARTI seeds).
            migrationBuilder.Sql(@"
INSERT INTO [CommodityAliases] ([Id], [Alias], [CropId], [Source], [Language], [IsActive], [CreatedAtUtc])
SELECT NEWID(), v.[Alias], c.[Id], 'DAMBULLA_DEC', NULL, 1, SYSUTCDATETIME()
FROM (VALUES
        ('1',   'VEG000014'),
        ('2',   'VEG000017'),
        ('3',   'VEG000054'),
        ('4',   'VEG000003'),
        ('6',   'VEG000043'),
        ('7',   'VEG000016'),
        ('8',   'VEG000004'),
        ('9',   'VEG000005'),
        ('10',  'VEG000006'),
        ('11',  'VEG000065'),
        ('13',  'VEG000053'),
        ('14',  'VEG000022'),
        ('15',  'VEG000049'),
        ('16',  'VEG000048'),
        ('18',  'VEG000050'),
        ('19',  'VEG000036'),
        ('20',  'VEG000047'),
        ('21',  'VEG000057'),
        ('22',  'VEG000035'),
        ('23',  'VEG000059'),
        ('24',  'VEG000010'),
        ('25',  'VEG000052'),
        ('26',  'VEG000012'),
        ('27',  'FRT000013'),
        ('28',  'VEG000028'),
        ('29',  'VEG000066'),
        ('30',  'FRT000011'),
        ('31',  'VEG000055'),
        ('32',  'VEG000029'),
        ('33',  'FRT000002'),
        ('34',  'FRT000009'),
        ('35',  'FRT000025'),
        ('36',  'FRT000018'),
        ('37',  'FRT000023'),
        ('38',  'FRT000021'),
        ('39',  'FRT000026'),
        ('40',  'FRT000004'),
        ('41',  'FRT000006'),
        ('42',  'FRT000014'),
        ('43',  'FRT000020'),
        ('44',  'FRT000003'),
        ('45',  'VEG000045'),
        ('46',  'FRT000007'),
        ('47',  'VEG000007'),
        ('48',  'VEG000009'),
        ('49',  'VEG000025'),
        ('50',  'VEG000011'),
        ('51',  'VEG000042'),
        ('52',  'VEG000051'),
        ('53',  'VEG000019'),
        ('54',  'VEG000064'),
        ('55',  'VEG000013'),
        ('56',  'VEG000063'),
        ('57',  'VEG000062'),
        ('58',  'VEG000026'),
        ('59',  'VEG000069'),
        ('60',  'VEG000070'),
        ('61',  'VEG000024'),
        ('62',  'VEG000001'),
        ('63',  'VEG000046'),
        ('64',  'FRT000001'),
        ('65',  'VEG000018'),
        ('66',  'VEG000015'),
        ('67',  'VEG000031'),
        ('68',  'VEG000041'),
        ('69',  'VEG000056'),
        ('70',  'VEG000008'),
        ('71',  'FRT000005'),
        ('72',  'FRT000022'),
        ('73',  'FRT000015'),
        ('74',  'FRT000017'),
        ('75',  'FRT000016'),
        ('76',  'FRT000019'),
        ('77',  'FRT000008'),
        ('78',  'FRT000010'),
        ('79',  'FRT000012'),
        ('80',  'VEG000030'),
        ('81',  'VEG000027'),
        ('82',  'VEG000021'),
        ('83',  'VEG000032'),
        ('84',  'VEG000058'),
        ('85',  'FRT000024'),
        ('86',  'VEG000060'),
        ('87',  'VEG000020'),
        ('89',  'VEG000067'),
        ('91',  'VEG000002'),
        ('92',  'VEG000023'),
        ('93',  'VEG000037'),
        ('94',  'VEG000040'),
        ('95',  'VEG000044'),
        ('96',  'VEG000068'),
        ('97',  'VEG000061'),
        ('98',  'VEG000039'),
        ('99',  'VEG000033'),
        ('100', 'VEG000034'),
        ('101', 'VEG000038')
     ) AS v([Alias], [CropCode])
INNER JOIN [Crops] c ON c.[CropCode] = v.[CropCode]
WHERE NOT EXISTS (
        SELECT 1 FROM [CommodityAliases] a
        WHERE a.[Alias] = v.[Alias] AND a.[Source] = 'DAMBULLA_DEC'
      );
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse ONLY the DEC aliases this migration seeds — never the HARTI rows nor any
            // user-added Source='DAMBULLA_DEC' rows. Keyed on the exact (Alias, Source) pairs.
            // (Delete by the full seeded ProductId list rather than a blanket Source delete, so a
            // future divergent DEC alias added by hand is left intact.)
            migrationBuilder.Sql(@"
DELETE FROM [CommodityAliases]
WHERE [Source] = 'DAMBULLA_DEC'
  AND [Alias] IN (
        '1','2','3','4','6','7','8','9','10','11','13','14','15','16','18','19','20','21','22',
        '23','24','25','26','27','28','29','30','31','32','33','34','35','36','37','38','39','40',
        '41','42','43','44','45','46','47','48','49','50','51','52','53','54','55','56','57','58',
        '59','60','61','62','63','64','65','66','67','68','69','70','71','72','73','74','75','76',
        '77','78','79','80','81','82','83','84','85','86','87','89','91','92','93','94','95','96',
        '97','98','99','100','101'
      );
");
        }
    }
}
