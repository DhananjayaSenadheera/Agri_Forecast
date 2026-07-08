using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillMarketPriceEconomicCenterAndAddFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // R2 Step 3.3 — per-source backfill of MarketPrices.EconomicCenterId → the
            // Dambulla Dedicated Economic Centre (Markets row, MarketCode 'MKT00000001'),
            // resolved via a code-keyed subquery (NEVER a hardcoded GUID). Backfills are
            // PER-SOURCE, never blanket (R2 pin). Both live sources are Dambulla prices per
            // hub adjudication: DAMBULLA_DEC is the live Dambulla economic-centre API; the
            // HARTI PDF backfill was a Dambulla-only splice, so those rows ARE Dambulla
            // wholesale prices. Runs BEFORE AddForeignKey so the FK never sees an
            // unresolved value.

            // DAMBULLA_DEC — live Dambulla economic-centre API feed.
            migrationBuilder.Sql(@"
UPDATE MarketPrices
SET EconomicCenterId = (SELECT Id FROM Markets WHERE MarketCode = 'MKT00000001')
WHERE Source = 'DAMBULLA_DEC' AND EconomicCenterId IS NULL;");

            // HARTI — PDF backfill, Dambulla-only splice (see CONTRACTS.md MarketPrices splice).
            migrationBuilder.Sql(@"
UPDATE MarketPrices
SET EconomicCenterId = (SELECT Id FROM Markets WHERE MarketCode = 'MKT00000001')
WHERE Source = 'HARTI' AND EconomicCenterId IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_MarketPrices_EconomicCenterId",
                table: "MarketPrices",
                column: "EconomicCenterId");

            migrationBuilder.AddForeignKey(
                name: "FK_MarketPrices_Markets_EconomicCenterId",
                table: "MarketPrices",
                column: "EconomicCenterId",
                principalTable: "Markets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MarketPrices_Markets_EconomicCenterId",
                table: "MarketPrices");

            migrationBuilder.DropIndex(
                name: "IX_MarketPrices_EconomicCenterId",
                table: "MarketPrices");

            // Reverse the per-source backfill: reset ONLY the rows this migration linked to
            // the Dambulla market, per source, back to NULL (never a blanket UPDATE).
            migrationBuilder.Sql(@"
UPDATE MarketPrices
SET EconomicCenterId = NULL
WHERE Source = 'DAMBULLA_DEC'
  AND EconomicCenterId = (SELECT Id FROM Markets WHERE MarketCode = 'MKT00000001');");

            migrationBuilder.Sql(@"
UPDATE MarketPrices
SET EconomicCenterId = NULL
WHERE Source = 'HARTI'
  AND EconomicCenterId = (SELECT Id FROM Markets WHERE MarketCode = 'MKT00000001');");
        }
    }
}
