using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCropPriceAndWeatherRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CropPrices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CropId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EconomicCenterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Month = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AveragePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CropPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CropPrices_Crops_CropId",
                        column: x => x.CropId,
                        principalTable: "Crops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CropPrices_EconomicCenters_EconomicCenterId",
                        column: x => x.EconomicCenterId,
                        principalTable: "EconomicCenters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WeatherRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Month = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AvgTemperature = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalRainfall = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CropPrices_CropId_EconomicCenterId_Month",
                table: "CropPrices",
                columns: new[] { "CropId", "EconomicCenterId", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CropPrices_EconomicCenterId",
                table: "CropPrices",
                column: "EconomicCenterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CropPrices");

            migrationBuilder.DropTable(
                name: "WeatherRecords");
        }
    }
}
