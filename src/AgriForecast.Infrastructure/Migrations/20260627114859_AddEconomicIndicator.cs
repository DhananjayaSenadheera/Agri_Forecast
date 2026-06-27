using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEconomicIndicator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EconomicIndicators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IndicatorCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EconomicIndicators", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EconomicIndicators_Date_IndicatorCode",
                table: "EconomicIndicators",
                columns: new[] { "Date", "IndicatorCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EconomicIndicators");
        }
    }
}
