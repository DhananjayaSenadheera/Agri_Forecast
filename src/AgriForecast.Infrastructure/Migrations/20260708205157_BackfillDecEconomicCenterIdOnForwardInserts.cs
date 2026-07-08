using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillDecEconomicCenterIdOnForwardInserts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Step 3.3 backfill (20260706163820) linked all rows that existed at the time,
            // but MarketPriceIngestionService kept inserting DAMBULLA_DEC rows without
            // EconomicCenterId, so days ingested after 2026-07-04 reopened the gap (352 rows
            // live). The service now stamps the link on insert; this closes the gap for rows
            // written in between. Per-source, code-keyed subquery — same rules as 20260706163820.
            migrationBuilder.Sql(@"
UPDATE MarketPrices
SET EconomicCenterId = (SELECT Id FROM Markets WHERE MarketCode = 'MKT00000001')
WHERE Source = 'DAMBULLA_DEC' AND EconomicCenterId IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible row-precisely: once linked, these rows are indistinguishable from
            // the Step 3.3 backfill's. Reverting to NULL here would also unlink rows that
            // migration owns, so Down is a deliberate no-op (matches the one-way precedent of
            // 20260706162817). Rolling back further is handled by 20260706163820's own Down.
        }
    }
}
