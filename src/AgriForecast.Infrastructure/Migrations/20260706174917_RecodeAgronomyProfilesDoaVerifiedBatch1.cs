using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecodeAgronomyProfilesDoaVerifiedBatch1 : Migration
    {
        // R2 Step 5.3 (Phase 1) — writes owner-signed-off (2026-07-06) DOA/DEA-verified
        // agronomy values into CropAgronomyProfiles for 13 of the model crops.
        //
        // Data-only migration (no schema/snapshot change). Each UPDATE is keyed on the
        // portable, ASCII-safe, unique Crops.CropCode (post Step-4 recode) — NOT on GUID
        // (per-database, auto-provisioned) and NOT on Name (avoids the apostrophe in
        // "Lady's Fingers" and the duplicate "Passion" name). Values, planting months and
        // per-crop DataSource citations are the reconciled Step-5 research figures.
        //
        // Up():   sets IsVerified=1, VerifiedOn='2026-07-06', the curated agronomy values,
        //         the DOA/DEA citation, and UpdatedAt=SYSUTCDATETIME().
        // Down(): restores each crop's PRE-migration state — IsVerified=0, VerifiedOn=NULL,
        //         DataSource='legacy-crops-table', all four month columns NULL,
        //         HarvestWindowDays NULL, IsPerennial=0, and GrowthPeriodDays back to its
        //         prior legacy value.
        //
        // The other 83 profiles are untouched (remain IsVerified=0). This is Batch 1.

        private static string Up(
            string cropCode, string gp, string hw, string ys, string ye,
            string ms, string me, string source)
        {
            // NULL-literal helper: callers pass "NULL" for null-valued columns.
            return
                $"UPDATE p SET p.GrowthPeriodDays={gp}, p.HarvestWindowDays={hw}, " +
                $"p.YalaPlantingStartMonth={ys}, p.YalaPlantingEndMonth={ye}, " +
                $"p.MahaPlantingStartMonth={ms}, p.MahaPlantingEndMonth={me}, " +
                $"p.IsPerennial=0, p.IsVerified=1, p.VerifiedOn='2026-07-06', " +
                $"p.DataSource=N'{source.Replace("'", "''")}', p.UpdatedAt=SYSUTCDATETIME() " +
                $"FROM CropAgronomyProfiles p JOIN Crops c ON c.Id=p.CropId " +
                $"WHERE c.CropCode='{cropCode}';";
        }

        private static string Down(string cropCode, string priorGp)
        {
            return
                $"UPDATE p SET p.GrowthPeriodDays={priorGp}, p.HarvestWindowDays=NULL, " +
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
            // code, gp, hw, yala start/end, maha start/end, DataSource citation
            migrationBuilder.Sql(Up("VEG000003", "60", "30", "3", "4", "11", "12",
                "DOA HORDI https://doa.gov.lk/hordi-crop-bean/ — vine type 60d; Yala Mar-Apr, Maha Nov-Dec (Badulla). HarvestWindow ~30d from picking cadence."));
            migrationBuilder.Sql(Up("VEG000059", "65", "45", "3", "4", "10", "11",
                "DOA HORDI https://doa.gov.lk/hordi-crop-snake-gourd/ — 60-75d→65. Months inferred from DOA season note + national Yala/Maha calendar. HW ~45d."));
            migrationBuilder.Sql(Up("VEG000057", "65", "50", "3", "4", "10", "11",
                "DOA HORDI https://doa.gov.lk/hordi-crop-luffa/ — 60-70d→65. HW 'harvest over 1.5-2 months'→~50d. Months inferred (DOA names season only)."));
            migrationBuilder.Sql(Up("VEG000010", "65", "45", "4", "5", "10", "11",
                "DOA HORDI https://doa.gov.lk/sinhala-hordi-crop-bitter-gourd/ — 60-75d→65. HW ~10-14 picks @4d→~45d. Yala Apr-May, Maha Oct-Nov (DOA explicit)."));
            migrationBuilder.Sql(Up("VEG000015", "75", "50", "4", "5", "11", "12",
                "DOA HORDI https://doa.gov.lk/hordi-crop-capsicum/ — first harvest 75d after nursery sowing. HW 7-10 picks @5-7d→~50d. Yala Apr-May, Maha Nov-Dec."));
            migrationBuilder.Sql(Up("VEG000035", "50", "50", "4", "5", "9", "10",
                "DOA HORDI https://doa.gov.lk/hordi-crop-okra/ — first harvest ~50d. HW 50d (picks every 2d to ~day 100). Yala Apr-May, Maha Sep-Oct (DOA explicit)."));
            migrationBuilder.Sql(Up("VEG000012", "95", "70", "4", "5", "11", "12",
                "DOA HORDI https://doa.gov.lk/hordi-crop-brinjal/ — 10-12wk after transplant + 25-30d nursery ≈95d. HW 10-12 picks @7d→~70d. Months = SL solanaceous consensus."));
            migrationBuilder.Sql(Up("VEG000019", "65", "NULL", "3", "5", "10", "12",
                "DOA HORDI https://doa.gov.lk/hordi-crop-kekiri/ — no day count (year-round); gp=65 FALLBACK from Cucumis melo refs (60-70d). HW=null (undocumented). LOW confidence."));
            migrationBuilder.Sql(Up("VEG000031", "75", "50", "4", "5", "11", "12",
                "DOA HORDI Capsicum page (proxy — same C. annuum) https://doa.gov.lk/hordi-crop-capsicum/ — 75d, HW ~50d, Yala Apr-May, Maha Nov-Dec. MEDIUM (proxy)."));
            migrationBuilder.Sql(Up("VEG000065", "65", "30", "3", "4", "8", "9",
                "DOA HORDI https://doa.gov.lk/hordi-crop-tomato/ — seasons+nursery only, no day count; gp=65 + HW 30 FALLBACK (FAO determinate 60-70d). Yala/Maha from DOA nursery timing. MEDIUM."));
            migrationBuilder.Sql(Up("FRT000025", "78", "30", "3", "5", "NULL", "NULL",
                "DOA https://doa.gov.lk/fruit-crops-watermelon-e/ — 75-80d first harvest→78. HW ~30d (to ~day 110). YALA ONLY (after Maha rains)→Maha=null."));
            migrationBuilder.Sql(Up("VEG000069", "73", "45", "3", "5", "10", "12",
                "DOA HORDI https://doa.gov.lk/hordi-crop-winged-bean/ — SLS44/Krishna 70-75d→73. HW 6-7wk→~45d. Months inferred (DOA names no months)."));
            migrationBuilder.Sql(Up("VEG000028", "270", "NULL", "3", "4", "9", "10",
                "DEA https://dea.gov.lk/ginger/ — harvest 8-10mo→270d. Plant mid-Mar-Apr or Sep-Oct. HW=null (single lifting). DEA (export-ag), not HORDI."));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore each crop's prior GrowthPeriodDays (legacy value) and null the rest.
            migrationBuilder.Sql(Down("VEG000003", "NULL")); // Beans
            migrationBuilder.Sql(Down("VEG000059", "NULL")); // Snake Gourd
            migrationBuilder.Sql(Down("VEG000057", "60"));   // Ridge Gourd
            migrationBuilder.Sql(Down("VEG000010", "60"));   // Bitter Gourd
            migrationBuilder.Sql(Down("VEG000015", "90"));   // Capsicum
            migrationBuilder.Sql(Down("VEG000035", "55"));   // Lady's Fingers
            migrationBuilder.Sql(Down("VEG000012", "90"));   // Brinjal
            migrationBuilder.Sql(Down("VEG000019", "65"));   // Cooking Melon
            migrationBuilder.Sql(Down("VEG000031", "90"));   // Green Chili
            migrationBuilder.Sql(Down("VEG000065", "75"));   // Tomato
            migrationBuilder.Sql(Down("FRT000025", "85"));   // Watermelon
            migrationBuilder.Sql(Down("VEG000069", "75"));   // Winged Bean
            migrationBuilder.Sql(Down("VEG000028", "240"));  // Ginger
        }
    }
}
