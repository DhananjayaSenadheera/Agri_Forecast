using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RepointCropPriceToMarketAndDropEcoCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CropPrices_EconomicCenters_EconomicCenterId",
                table: "CropPrices");

            migrationBuilder.DropColumn(
                name: "Eco_Code",
                table: "DefaultSettings");

            migrationBuilder.DropColumn(
                name: "Eco_Padding",
                table: "DefaultSettings");

            migrationBuilder.DropColumn(
                name: "Eco_Prefix",
                table: "DefaultSettings");

            migrationBuilder.AddForeignKey(
                name: "FK_CropPrices_Markets_EconomicCenterId",
                table: "CropPrices",
                column: "EconomicCenterId",
                principalTable: "Markets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CropPrices_Markets_EconomicCenterId",
                table: "CropPrices");

            migrationBuilder.AddColumn<int>(
                name: "Eco_Code",
                table: "DefaultSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Eco_Padding",
                table: "DefaultSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Eco_Prefix",
                table: "DefaultSettings",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "DefaultSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "Eco_Code", "Eco_Padding", "Eco_Prefix" },
                values: new object[] { 1, 8, "ECO" });

            migrationBuilder.AddForeignKey(
                name: "FK_CropPrices_EconomicCenters_EconomicCenterId",
                table: "CropPrices",
                column: "EconomicCenterId",
                principalTable: "EconomicCenters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
