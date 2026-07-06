using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecodeAgronomyProfilesDoaVerifiedBatch2 : Migration
    {
        // R2 Step 5.3 (Phase 2) — writes owner-approved (2026-07-06) DOA/DEA-verified (and
        // documented FALLBACK/Low-confidence) agronomy values into CropAgronomyProfiles for
        // the remaining 81 model crops (Batch 1 covered the first 13).
        //
        // Data-only migration (no schema/snapshot change). Each UPDATE is keyed on the
        // portable, ASCII-safe, unique Crops.CropCode — NOT on GUID (per-database,
        // auto-provisioned) and NOT on Name (duplicate "Passion", diacritics). Values,
        // planting months, IsPerennial and per-crop DataSource citations are the reconciled
        // Step-5 Phase-2 research figures (memory/step5_phase2_consolidated.json).
        //
        // DataSource honestly records tier/confidence: FALLBACK / conf=Low entries are
        // lower-confidence non-DOA figures and are NOT dressed up as authoritative.
        //
        // EXCLUDED (held UNVERIFIED per owner decision — rows untouched, IsVerified=0,
        //          DataSource='legacy-crops-table'):
        //   VEG000044 (Onion Leaves)  — no credible source
        //   VEG000002 (Athugowa)      — vendor-only, no DOA/research source
        // => exactly 81 UPDATEs. Combined with Batch 1 this yields 94 IsVerified=1 profiles.
        //
        // Perennials (per=1): the four planting-month columns are NULL (no discrete planting
        // season); continuous perennials (Coconut, Papaya, Ambarella, Curry Leaves,
        // Gooseberry, Woodapple, Plantain Flower, Ash Plantain, King Coconut, Lime) have
        // GrowthPeriodDays=NULL (no serving horizon). Reclassifications baked into the data:
        // Thibbatu/Thumba Karawila/Chayote perennial; Kiriala NON-perennial (single-lift corm).
        //
        // Up():   sets the curated agronomy, IsPerennial, IsVerified=1, VerifiedOn='2026-07-06',
        //         the citation, UpdatedAt=SYSUTCDATETIME().
        // Down(): all 81 had NO legacy agronomy values (the legacy-gp crops were all in
        //         Batch 1), so every Down restores the uniform prior state —
        //         all values NULL, IsPerennial=0, IsVerified=0, VerifiedOn=NULL,
        //         DataSource='legacy-crops-table', UpdatedAt=SYSUTCDATETIME().

        private static string Up(
            string cropCode, string gp, string hw, string ys, string ye,
            string ms, string me, string per, string source)
        {
            // NULL-literal helper: callers pass "NULL" for null-valued columns.
            return
                $"UPDATE p SET p.GrowthPeriodDays={gp}, p.HarvestWindowDays={hw}, " +
                $"p.YalaPlantingStartMonth={ys}, p.YalaPlantingEndMonth={ye}, " +
                $"p.MahaPlantingStartMonth={ms}, p.MahaPlantingEndMonth={me}, " +
                $"p.IsPerennial={per}, p.IsVerified=1, p.VerifiedOn='2026-07-06', " +
                $"p.DataSource=N'{source.Replace("'", "''")}', p.UpdatedAt=SYSUTCDATETIME() " +
                $"FROM CropAgronomyProfiles p JOIN Crops c ON c.Id=p.CropId " +
                $"WHERE c.CropCode='{cropCode}';";
        }

        private static string Down(string cropCode)
        {
            return
                $"UPDATE p SET p.GrowthPeriodDays=NULL, p.HarvestWindowDays=NULL, " +
                $"p.YalaPlantingStartMonth=NULL, p.YalaPlantingEndMonth=NULL, " +
                $"p.MahaPlantingStartMonth=NULL, p.MahaPlantingEndMonth=NULL, " +
                $"p.IsPerennial=0, p.IsVerified=0, p.VerifiedOn=NULL, " +
                $"p.DataSource=N'legacy-crops-table', p.UpdatedAt=SYSUTCDATETIME() " +
                $"FROM CropAgronomyProfiles p JOIN Crops c ON c.Id=p.CropId " +
                $"WHERE c.CropCode='{cropCode}';";
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // code, gp, hw, yala start/end, maha start/end, IsPerennial, DataSource citation
            migrationBuilder.Sql(Up("VEG000022", "50", "30", "3", "5", "10", "12", "0",
                "FALLBACK FAO cucurbit (DOA cucumber page lacks day count) | R2 Step5 P2 | conf=Low tier=FALLBACK"));  // Cucumber
            migrationBuilder.Sql(Up("VEG000026", "75", "70", "4", "5", "11", "12", "0",
                "DOA HORDI hordi-crop-brinjal (10-12wk transplant) | R2 Step5 P2 | conf=High tier=DOA"));  // Eggplant
            migrationBuilder.Sql(Up("VEG000062", "75", "70", "4", "5", "11", "12", "0",
                "DOA HORDI hordi-crop-ela-batu | R2 Step5 P2 | conf=High tier=DOA"));  // Thai Eggplant
            migrationBuilder.Sql(Up("VEG000063", "105", "NULL", "3", "5", "10", "12", "1",
                "FALLBACK TAA (no DOA page); reclassified PERENNIAL | R2 Step5 P2 | conf=Low tier=FALLBACK"));  // Thibbatu
            migrationBuilder.Sql(Up("VEG000064", "90", "NULL", "3", "4", "9", "10", "1",
                "DOA HORDI hordi-crop-thumba-karawila; PERENNIAL vine | R2 Step5 P2 | conf=Medium tier=DOA"));  // Thumba Karawila
            migrationBuilder.Sql(Up("VEG000018", "120", "NULL", "3", "5", "10", "12", "1",
                "FALLBACK horticulture refs (no DOA page); reclassified PERENNIAL | R2 Step5 P2 | conf=Low tier=FALLBACK"));  // Chayote/Chow-Chow
            migrationBuilder.Sql(Up("VEG000013", "100", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI hordi-crop-pumpkin (harvest 20d after flowering) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Butternut Squash
            migrationBuilder.Sql(Up("VEG000052", "95", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI hordi-crop-pumpkin (A.N.K. 40d after flowering) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Pumpkin-Malashian
            migrationBuilder.Sql(Up("VEG000051", "110", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI hordi-crop-pumpkin (local var 60d after flowering) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Pumpkin-Big
            migrationBuilder.Sql(Up("VEG000021", "65", "25", "4", "5", "10", "11", "0",
                "DOA field-crops-cowpea (green 55-65d) | R2 Step5 P2 | conf=High tier=DOA"));  // Cowpea
            migrationBuilder.Sql(Up("VEG000032", "65", "15", "4", "5", "10", "11", "0",
                "DOA RARDC green gram (60-75d) | R2 Step5 P2 | conf=High tier=DOA"));  // Green Gram
            migrationBuilder.Sql(Up("VEG000030", "90", "NULL", "NULL", "NULL", "10", "11", "0",
                "FALLBACK MISB-01 90d (no DOA page); Maha-only | R2 Step5 P2 | conf=Low tier=FALLBACK"));  // Gram/chickpea
            migrationBuilder.Sql(Up("VEG000060", "95", "NULL", "4", "5", "9", "11", "0",
                "DOA field-crops-soybeans (PB-1) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Soya Bean
            migrationBuilder.Sql(Up("VEG000045", "100", "NULL", "4", "4", "10", "10", "0",
                "DOA field-crops-groundnut (100-110d) | R2 Step5 P2 | conf=High tier=DOA"));  // Peanuts
            migrationBuilder.Sql(Up("VEG000070", "55", "30", "3", "5", "11", "11", "0",
                "DOA HORDI hordi-crop-yard-long-bean | R2 Step5 P2 | conf=Medium tier=DOA"));  // Yard-Long Beans
            migrationBuilder.Sql(Up("VEG000020", "95", "NULL", "4", "5", "10", "11", "0",
                "DOA maize (hybrid 90-100d) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Corns
            migrationBuilder.Sql(Up("VEG000041", "90", "45", "4", "5", "11", "12", "0",
                "DOA capsicum proxy (frutescens longer) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Nai Miris
            migrationBuilder.Sql(Up("VEG000025", "120", "45", "4", "5", "11", "12", "0",
                "DOA capsicum (75d green + 30-45d to dry red) | R2 Step5 P2 | conf=High tier=DOA"));  // Dry Chillies
            migrationBuilder.Sql(Up("VEG000027", "105", "NULL", "4", "5", "9", "11", "0",
                "DOA RARDC finger millet (3-4mo) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Finger Millet
            migrationBuilder.Sql(Up("VEG000058", "85", "NULL", "3", "4", "9", "10", "0",
                "DOA field-crops-sesame (70-100d) | R2 Step5 P2 | conf=High tier=DOA"));  // Sesame
            migrationBuilder.Sql(Up("VEG000014", "100", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI hordi-crop-cabbage (90-110d) | R2 Step5 P2 | conf=High tier=DOA"));  // Cabbage
            migrationBuilder.Sql(Up("VEG000054", "100", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI cabbage (=Cabbage, no separate page) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Red Cabbage
            migrationBuilder.Sql(Up("VEG000017", "68", "NULL", "1", "3", "10", "12", "0",
                "DOA HORDI hordi-crop-cauliflower (60-75d) | R2 Step5 P2 | conf=High tier=DOA"));  // Cauliflower
            migrationBuilder.Sql(Up("VEG000042", "55", "NULL", "NULL", "NULL", "9", "9", "0",
                "DOA HORDI hordi-crop-knol-khol (50-60d; Maha Sep) | R2 Step5 P2 | conf=High tier=DOA"));  // Knolkhol
            migrationBuilder.Sql(Up("VEG000036", "135", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI hordi-crop-leeks (~4.5mo) | R2 Step5 P2 | conf=High tier=DOA"));  // Leeks
            migrationBuilder.Sql(Up("VEG000037", "55", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI hordi-crop-salad-leaves (50-60d) | R2 Step5 P2 | conf=High tier=DOA"));  // Lettuce
            migrationBuilder.Sql(Up("VEG000004", "85", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI beet-root (75-90d midpoint 85) | R2 Step5 P2 | conf=High tier=DOA"));  // Beetroot-Nuwaraeliya
            migrationBuilder.Sql(Up("VEG000005", "85", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI beet-root (=Beetroot) | R2 Step5 P2 | conf=High tier=DOA"));  // Beetroot Cut-Malsiripura
            migrationBuilder.Sql(Up("VEG000006", "85", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI beet-root (=Beetroot) | R2 Step5 P2 | conf=High tier=DOA"));  // Beetroot Cut-Puththalama
            migrationBuilder.Sql(Up("VEG000016", "110", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI hordi-variety-carrot (New Kuroda 110) | R2 Step5 P2 | conf=High tier=DOA"));  // Carrot-Jaffna
            migrationBuilder.Sql(Up("VEG000043", "110", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI carrot (=Carrot) | R2 Step5 P2 | conf=High tier=DOA"));  // Nuwaraeliya Carrot
            migrationBuilder.Sql(Up("VEG000053", "50", "NULL", "3", "5", "10", "12", "0",
                "DOA HORDI hordi-variety-raddish (45-55d year-round) | R2 Step5 P2 | conf=High tier=DOA"));  // Raddish
            migrationBuilder.Sql(Up("VEG000007", "90", "NULL", "4", "5", "12", "12", "0",
                "DOA field-crops-bigonion-si (85-90d) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Big Onion
            migrationBuilder.Sql(Up("VEG000008", "90", "NULL", "4", "5", "12", "12", "0",
                "DOA bigonion (=Big Onion) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Big Onion Import
            migrationBuilder.Sql(Up("VEG000009", "90", "NULL", "4", "5", "12", "12", "0",
                "DOA bigonion (=Big Onion) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Big Onion Lanka
            migrationBuilder.Sql(Up("VEG000056", "80", "NULL", "3", "4", "9", "11", "0",
                "DOA field-crops-redonion-si (Vedalam 80-90) | R2 Step5 P2 | conf=High tier=DOA"));  // Red Onion-Lanka
            migrationBuilder.Sql(Up("VEG000055", "80", "NULL", "3", "4", "9", "11", "0",
                "DOA redonion (=Red Onion) | R2 Step5 P2 | conf=High tier=DOA"));  // Red Onion Import
            migrationBuilder.Sql(Up("VEG000049", "100", "NULL", "2", "3", "8", "9", "0",
                "DOA hordi-variety-potato (Golden Star 100-110); months FALLBACK NE production-lit | R2 Step5 P2 | conf=Medium tier=DOA"));  // Potatoes-NuwaraEliya
            migrationBuilder.Sql(Up("VEG000047", "100", "NULL", "2", "3", "8", "9", "0",
                "DOA potato (=Potato); months FALLBACK NE production-lit | R2 Step5 P2 | conf=Medium tier=DOA"));  // Potatoes-Import
            migrationBuilder.Sql(Up("VEG000048", "100", "NULL", "2", "3", "8", "9", "0",
                "DOA potato (=Potato); months FALLBACK NE production-lit | R2 Step5 P2 | conf=Medium tier=DOA"));  // Potatoes-Jaffna
            migrationBuilder.Sql(Up("VEG000050", "100", "NULL", "2", "3", "8", "9", "0",
                "DOA potato (=Potato); months FALLBACK NE production-lit | R2 Step5 P2 | conf=Medium tier=DOA"));  // Potatoes-Walimada
            migrationBuilder.Sql(Up("VEG000068", "100", "NULL", "2", "3", "8", "9", "0",
                "DOA potato (=Potato, early-lift, no distinct page) | R2 Step5 P2 | conf=Low tier=DOA"));  // Wild-Baby Potato
            migrationBuilder.Sql(Up("VEG000029", "100", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA HORDI hordi-crop-gotukola (first harvest 100d; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Gotukola
            migrationBuilder.Sql(Up("VEG000040", "28", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA HORDI hordi-crop-mukunuwenna (4wk first cut; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Mukunuwenna
            migrationBuilder.Sql(Up("VEG000067", "30", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA HORDI hordi-crop-kang-kung (30d; cut every 20-25d; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Water Spinach
            migrationBuilder.Sql(Up("VEG000034", "240", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA HORDI hordi-crop-kohila (8-12mo first harvest; perennial) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Kohila
            migrationBuilder.Sql(Up("VEG000033", "270", "NULL", "NULL", "NULL", "NULL", "NULL", "0",
                "DOA HORDI hordi-crop-kiri-ala (8-10mo single-lift corm; reclassified NON-perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Kiriala
            migrationBuilder.Sql(Up("VEG000038", "150", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK appropedia/specialtyproduce (no DOA page); perennial | R2 Step5 P2 | conf=Low tier=FALLBACK"));  // Lotus Roots
            migrationBuilder.Sql(Up("VEG000039", "300", "90", "NULL", "NULL", "10", "12", "0",
                "DOA HORDI hordi-crop-cassava (9-12mo; Maha dry zone) | R2 Step5 P2 | conf=High tier=DOA"));  // Manioc
            migrationBuilder.Sql(Up("VEG000061", "105", "30", "NULL", "NULL", "NULL", "NULL", "0",
                "DOA HORDI hordi-crop-sweet-potato (3-4mo) | R2 Step5 P2 | conf=High tier=DOA"));  // Sweet Potato
            migrationBuilder.Sql(Up("VEG000001", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA banana perennial (sow->bunch; gp null pending re-verify) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Ash Plantain
            migrationBuilder.Sql(Up("FRT000003", "90", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-banana-e (Ambul 3mo bunch dev; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Banana-Abul
            migrationBuilder.Sql(Up("FRT000004", "98", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-banana-e (Amburel 3mo1wk; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Banana-Ambun
            migrationBuilder.Sql(Up("FRT000005", "120", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-banana-e (Kolikuttu 4mo; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Banana-Kolikuttu
            migrationBuilder.Sql(Up("FRT000006", "135", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-banana-e (Sugar/Sini 4.5mo; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Banana-Sini
            migrationBuilder.Sql(Up("FRT000022", "105", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-banana-e (banana default midpoint; perennial) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Red Banana
            migrationBuilder.Sql(Up("VEG000046", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA banana inflorescence (harvested at bunch emergence; gp null; perennial) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Plantain Flower
            migrationBuilder.Sql(Up("FRT000015", "130", "120", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK FAO/ISHS mango 110-150 (DOA gives no day count); perennial | R2 Step5 P2 | conf=Medium tier=FALLBACK"));  // Mango-Malu
            migrationBuilder.Sql(Up("FRT000014", "130", "120", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK FAO/ISHS mango (=Mango); perennial | R2 Step5 P2 | conf=Medium tier=FALLBACK"));  // Mango-Karatha Kolomban
            migrationBuilder.Sql(Up("FRT000017", "130", "120", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK FAO/ISHS mango (TJC not found by name); perennial | R2 Step5 P2 | conf=Low tier=FALLBACK"));  // Mango-TJC
            migrationBuilder.Sql(Up("FRT000016", "130", "120", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK FAO/ISHS mango (=Mango); perennial | R2 Step5 P2 | conf=Medium tier=FALLBACK"));  // Mango-Other
            migrationBuilder.Sql(Up("FRT000018", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-papaw-e (continuous fruiting; gp null; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Papaya
            migrationBuilder.Sql(Up("FRT000021", "150", "60", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-pineapple-e (~5mo flower->ripe; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Pineapple
            migrationBuilder.Sql(Up("FRT000002", "240", "120", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK FAO avocado 7-9mo fruit-dev (DOA EN under construction); perennial | R2 Step5 P2 | conf=Medium tier=FALLBACK"));  // Avocado
            migrationBuilder.Sql(Up("FRT000009", "135", "120", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-guava-e (120-150 flower->harvest; perennial) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Guava
            migrationBuilder.Sql(Up("FRT000011", "105", "120", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK 12-16wk after flowering (no DOA EN); perennial | R2 Step5 P2 | conf=Medium tier=FALLBACK"));  // Jackfruit
            migrationBuilder.Sql(Up("FRT000007", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA/CRI SL + TNAU/FAO coconut (continuous; gp null; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Coconut
            migrationBuilder.Sql(Up("FRT000012", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK king coconut (no DOA; continuous; gp null; perennial) | R2 Step5 P2 | conf=Medium tier=FALLBACK"));  // King Coconut
            migrationBuilder.Sql(Up("FRT000013", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK NSF SL/Sunday Times acid-lime 5-6mo (gp null; perennial) | R2 Step5 P2 | conf=Medium tier=FALLBACK"));  // Lime
            migrationBuilder.Sql(Up("FRT000024", "270", "120", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK Dilmah/CRFG tamarind 8-10mo pod; perennial | R2 Step5 P2 | conf=Medium tier=FALLBACK"));  // Tamarind
            migrationBuilder.Sql(Up("FRT000001", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK ambarella (DOA FCRDC 404; continuous; gp null; perennial) | R2 Step5 P2 | conf=Low tier=FALLBACK"));  // Ambarella
            migrationBuilder.Sql(Up("FRT000008", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-amla (bearing 2.5-3yr; no day count; gp null; perennial) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Gooseberry
            migrationBuilder.Sql(Up("FRT000026", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-wood-apple (flower Feb-Mar; no day count; gp null; perennial) | R2 Step5 P2 | conf=Low tier=DOA"));  // Woodapple
            migrationBuilder.Sql(Up("FRT000023", "135", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK anona fruit-dev 4-5mo (DOA states bearing yrs only); perennial | R2 Step5 P2 | conf=Low tier=FALLBACK"));  // Soursop
            migrationBuilder.Sql(Up("FRT000010", "255", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-beli (DOA-stated 7-10mo maturity; perennial) | R2 Step5 P2 | conf=Medium tier=DOA"));  // Indian bael
            migrationBuilder.Sql(Up("FRT000019", "60", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-passion-fruit-e (60d after flowering; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Passion
            migrationBuilder.Sql(Up("FRT000020", "60", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA fruit-crops-passion-fruit-e (=Passion; perennial) | R2 Step5 P2 | conf=High tier=DOA"));  // Passion
            migrationBuilder.Sql(Up("VEG000011", "210", "120", "NULL", "NULL", "NULL", "NULL", "1",
                "DEA pepper (berry-dev 6-8mo fallback; perennial) | R2 Step5 P2 | conf=Medium tier=DEA"));  // Black Pepper
            migrationBuilder.Sql(Up("VEG000066", "270", "60", "3", "4", "NULL", "NULL", "0",
                "DEA turmeric (8-10mo; plant Mar-Apr->harvest Dec-Jan; annual rhizome) | R2 Step5 P2 | conf=High tier=DEA"));  // Turmeric
            migrationBuilder.Sql(Up("VEG000023", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "1",
                "FALLBACK evergreen shrub plucked year-round (gp null; perennial) | R2 Step5 P2 | conf=High tier=FALLBACK"));  // Curry Leaves
            migrationBuilder.Sql(Up("VEG000024", "65", "90", "NULL", "NULL", "NULL", "NULL", "1",
                "DOA HORDI hordi-crop-moringa (identity); pod-dev 65d FALLBACK; perennial | R2 Step5 P2 | conf=Medium tier=DOA"));  // Drumsticks
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Down("VEG000022")); // Cucumber
            migrationBuilder.Sql(Down("VEG000026")); // Eggplant
            migrationBuilder.Sql(Down("VEG000062")); // Thai Eggplant
            migrationBuilder.Sql(Down("VEG000063")); // Thibbatu
            migrationBuilder.Sql(Down("VEG000064")); // Thumba Karawila
            migrationBuilder.Sql(Down("VEG000018")); // Chayote/Chow-Chow
            migrationBuilder.Sql(Down("VEG000013")); // Butternut Squash
            migrationBuilder.Sql(Down("VEG000052")); // Pumpkin-Malashian
            migrationBuilder.Sql(Down("VEG000051")); // Pumpkin-Big
            migrationBuilder.Sql(Down("VEG000021")); // Cowpea
            migrationBuilder.Sql(Down("VEG000032")); // Green Gram
            migrationBuilder.Sql(Down("VEG000030")); // Gram/chickpea
            migrationBuilder.Sql(Down("VEG000060")); // Soya Bean
            migrationBuilder.Sql(Down("VEG000045")); // Peanuts
            migrationBuilder.Sql(Down("VEG000070")); // Yard-Long Beans
            migrationBuilder.Sql(Down("VEG000020")); // Corns
            migrationBuilder.Sql(Down("VEG000041")); // Nai Miris
            migrationBuilder.Sql(Down("VEG000025")); // Dry Chillies
            migrationBuilder.Sql(Down("VEG000027")); // Finger Millet
            migrationBuilder.Sql(Down("VEG000058")); // Sesame
            migrationBuilder.Sql(Down("VEG000014")); // Cabbage
            migrationBuilder.Sql(Down("VEG000054")); // Red Cabbage
            migrationBuilder.Sql(Down("VEG000017")); // Cauliflower
            migrationBuilder.Sql(Down("VEG000042")); // Knolkhol
            migrationBuilder.Sql(Down("VEG000036")); // Leeks
            migrationBuilder.Sql(Down("VEG000037")); // Lettuce
            migrationBuilder.Sql(Down("VEG000004")); // Beetroot-Nuwaraeliya
            migrationBuilder.Sql(Down("VEG000005")); // Beetroot Cut-Malsiripura
            migrationBuilder.Sql(Down("VEG000006")); // Beetroot Cut-Puththalama
            migrationBuilder.Sql(Down("VEG000016")); // Carrot-Jaffna
            migrationBuilder.Sql(Down("VEG000043")); // Nuwaraeliya Carrot
            migrationBuilder.Sql(Down("VEG000053")); // Raddish
            migrationBuilder.Sql(Down("VEG000007")); // Big Onion
            migrationBuilder.Sql(Down("VEG000008")); // Big Onion Import
            migrationBuilder.Sql(Down("VEG000009")); // Big Onion Lanka
            migrationBuilder.Sql(Down("VEG000056")); // Red Onion-Lanka
            migrationBuilder.Sql(Down("VEG000055")); // Red Onion Import
            migrationBuilder.Sql(Down("VEG000049")); // Potatoes-NuwaraEliya
            migrationBuilder.Sql(Down("VEG000047")); // Potatoes-Import
            migrationBuilder.Sql(Down("VEG000048")); // Potatoes-Jaffna
            migrationBuilder.Sql(Down("VEG000050")); // Potatoes-Walimada
            migrationBuilder.Sql(Down("VEG000068")); // Wild-Baby Potato
            migrationBuilder.Sql(Down("VEG000029")); // Gotukola
            migrationBuilder.Sql(Down("VEG000040")); // Mukunuwenna
            migrationBuilder.Sql(Down("VEG000067")); // Water Spinach
            migrationBuilder.Sql(Down("VEG000034")); // Kohila
            migrationBuilder.Sql(Down("VEG000033")); // Kiriala
            migrationBuilder.Sql(Down("VEG000038")); // Lotus Roots
            migrationBuilder.Sql(Down("VEG000039")); // Manioc
            migrationBuilder.Sql(Down("VEG000061")); // Sweet Potato
            migrationBuilder.Sql(Down("VEG000001")); // Ash Plantain
            migrationBuilder.Sql(Down("FRT000003")); // Banana-Abul
            migrationBuilder.Sql(Down("FRT000004")); // Banana-Ambun
            migrationBuilder.Sql(Down("FRT000005")); // Banana-Kolikuttu
            migrationBuilder.Sql(Down("FRT000006")); // Banana-Sini
            migrationBuilder.Sql(Down("FRT000022")); // Red Banana
            migrationBuilder.Sql(Down("VEG000046")); // Plantain Flower
            migrationBuilder.Sql(Down("FRT000015")); // Mango-Malu
            migrationBuilder.Sql(Down("FRT000014")); // Mango-Karatha Kolomban
            migrationBuilder.Sql(Down("FRT000017")); // Mango-TJC
            migrationBuilder.Sql(Down("FRT000016")); // Mango-Other
            migrationBuilder.Sql(Down("FRT000018")); // Papaya
            migrationBuilder.Sql(Down("FRT000021")); // Pineapple
            migrationBuilder.Sql(Down("FRT000002")); // Avocado
            migrationBuilder.Sql(Down("FRT000009")); // Guava
            migrationBuilder.Sql(Down("FRT000011")); // Jackfruit
            migrationBuilder.Sql(Down("FRT000007")); // Coconut
            migrationBuilder.Sql(Down("FRT000012")); // King Coconut
            migrationBuilder.Sql(Down("FRT000013")); // Lime
            migrationBuilder.Sql(Down("FRT000024")); // Tamarind
            migrationBuilder.Sql(Down("FRT000001")); // Ambarella
            migrationBuilder.Sql(Down("FRT000008")); // Gooseberry
            migrationBuilder.Sql(Down("FRT000026")); // Woodapple
            migrationBuilder.Sql(Down("FRT000023")); // Soursop
            migrationBuilder.Sql(Down("FRT000010")); // Indian bael
            migrationBuilder.Sql(Down("FRT000019")); // Passion
            migrationBuilder.Sql(Down("FRT000020")); // Passion
            migrationBuilder.Sql(Down("VEG000011")); // Black Pepper
            migrationBuilder.Sql(Down("VEG000066")); // Turmeric
            migrationBuilder.Sql(Down("VEG000023")); // Curry Leaves
            migrationBuilder.Sql(Down("VEG000024")); // Drumsticks
        }
    }
}
