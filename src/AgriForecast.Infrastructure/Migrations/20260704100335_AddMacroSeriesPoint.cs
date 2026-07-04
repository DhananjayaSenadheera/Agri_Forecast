using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMacroSeriesPoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MacroSeriesPoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeriesCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReferenceDate = table.Column<DateTime>(type: "date", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "date", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsPublishedAtImputed = table.Column<bool>(type: "bit", nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MacroSeriesPoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MacroSeriesPoints_SeriesCode_PublishedAt",
                table: "MacroSeriesPoints",
                columns: new[] { "SeriesCode", "PublishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MacroSeriesPoints_SeriesCode_ReferenceDate",
                table: "MacroSeriesPoints",
                columns: new[] { "SeriesCode", "ReferenceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_MacroSeriesPoints_SeriesCode_ReferenceDate_PublishedAt",
                table: "MacroSeriesPoints",
                columns: new[] { "SeriesCode", "ReferenceDate", "PublishedAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MacroSeriesPoints");
        }
    }
}
