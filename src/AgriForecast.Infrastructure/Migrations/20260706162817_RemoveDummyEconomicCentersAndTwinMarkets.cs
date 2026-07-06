using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// R2 D-DF3, subtask 3.2. Removes the four dummy demo EconomicCenters (ECO00000002–ECO00000005)
    /// and their twin "ECOMAP-*" Markets that the old EconomicCenters CRUD stack minted.
    /// Keyed on the stable EcoCode business codes and the ECOMAP- MarketCode prefix (twin codes
    /// embed per-DB GUIDs, so a prefix match is the portable key). The real Dambulla economic-centre row
    /// (MKT00000001, IsEconomicCenter=1) and the EconomicCenters TABLE itself are NOT touched —
    /// table retirement is a later subtask.
    ///
    /// ORDER MATTERS: EconomicCenters carry an FK (Restrict) to their twin Market via
    /// EconomicCenters.MarketId, so the Eco rows must be deleted BEFORE the twin Markets, otherwise
    /// the Market delete violates FK_EconomicCenters_Markets_MarketId. Verified live before writing:
    /// CropPrices = 0 rows, and neither MarketPrices nor PriceObservations reference the twin Markets,
    /// so no price rows are orphaned.
    ///
    /// Down() is intentionally a NO-OP: these were fabricated demo rows with no analytical value;
    /// re-seeding them would re-introduce the very dummy data + twin FKs this migration removes.
    /// </remarks>
    public partial class RemoveDummyEconomicCentersAndTwinMarkets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Delete the four dummy EconomicCenters FIRST (they hold the FK to the twin Markets).
            migrationBuilder.Sql(@"
DELETE FROM EconomicCenters
WHERE EcoCode IN ('ECO00000002', 'ECO00000003', 'ECO00000004', 'ECO00000005');");

            // 2. Then delete the twin ECOMAP-* Markets (no longer referenced by any Eco row).
            //    ECOMAP codes embed the source EconomicCenter's per-DB GUID (see
            //    20260702174842_AddMultiMarketAndPointInTimeData), so exact-code keys are not
            //    portable across environments — the prefix pattern is the stable key, matching
            //    that migration's own Down().
            migrationBuilder.Sql(@"
DELETE FROM Markets
WHERE MarketCode LIKE 'ECOMAP-%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentional no-op — see class remarks. The deleted rows were fabricated demo data.
        }
    }
}
