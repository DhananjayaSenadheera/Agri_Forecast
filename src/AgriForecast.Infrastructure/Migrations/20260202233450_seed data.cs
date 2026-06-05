using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class seeddata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "DefaultSettings",
                columns: new[] { "Id", "Crop_Code", "Crop_Padding", "Crop_Prefix", "Eco_Code", "Eco_Padding", "Eco_Prefix" },
                values: new object[] { 1, 1, 8, "CROP", 1, 8, "ECO" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "DefaultSettings",
                keyColumn: "Id",
                keyValue: 1);
        }
    }
}
