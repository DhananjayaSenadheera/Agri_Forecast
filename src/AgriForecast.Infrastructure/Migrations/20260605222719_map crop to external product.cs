using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class mapcroptoexternalproduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQL Server cannot ALTER int -> uniqueidentifier; these columns were always
            // null (mapping was never implemented), so drop and re-add as Guid.
            migrationBuilder.DropColumn(
                name: "EconomicCenterId",
                table: "MarketPrices");

            migrationBuilder.DropColumn(
                name: "CropId",
                table: "MarketPrices");

            migrationBuilder.AddColumn<Guid>(
                name: "EconomicCenterId",
                table: "MarketPrices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CropId",
                table: "MarketPrices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExternalProductId",
                table: "Crops",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Crops",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalProductId",
                table: "Crops");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Crops");

            migrationBuilder.DropColumn(
                name: "EconomicCenterId",
                table: "MarketPrices");

            migrationBuilder.DropColumn(
                name: "CropId",
                table: "MarketPrices");

            migrationBuilder.AddColumn<int>(
                name: "EconomicCenterId",
                table: "MarketPrices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CropId",
                table: "MarketPrices",
                type: "int",
                nullable: true);
        }
    }
}
