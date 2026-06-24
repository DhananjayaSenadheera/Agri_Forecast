using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCropAgronomicMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GrowthPeriodDays",
                table: "Crops",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HarvestWindowDays",
                table: "Crops",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlantingSeason",
                table: "Crops",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrowthPeriodDays",
                table: "Crops");

            migrationBuilder.DropColumn(
                name: "HarvestWindowDays",
                table: "Crops");

            migrationBuilder.DropColumn(
                name: "PlantingSeason",
                table: "Crops");
        }
    }
}
