using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIsEconomicCenterToMarket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEconomicCenter",
                table: "Markets",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Flag the real Dedicated Economic Centre (Dambulla). Keyed on the stable
            // MarketCode business key — NEVER on a per-DB GUID and NEVER via HasData
            // (Market seed GUIDs are fixed but the flag is data, not seed identity;
            // R2 D-DF3 mandates a code-keyed UPDATE). Only MKT00000001 is a DEC economic
            // centre today; the other DEC-typed rows (Keppetipola/Thambuttegama) are
            // deliberately NOT flagged here — this migration flags exactly the one real
            // centre per spec 3.1. Idempotent: re-running sets the same value.
            migrationBuilder.Sql(
                "UPDATE [Markets] SET [IsEconomicCenter] = 1 WHERE [MarketCode] = 'MKT00000001';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsEconomicCenter",
                table: "Markets");
        }
    }
}
