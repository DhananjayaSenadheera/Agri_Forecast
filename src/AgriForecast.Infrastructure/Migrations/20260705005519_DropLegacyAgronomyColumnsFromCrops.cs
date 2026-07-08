using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyAgronomyColumnsFromCrops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // R2 Step 2.4: CropAgronomyProfiles (added in Step 2.1, cut over in Python in
            // Step 2.3) is now the sole owner of agronomy. Drop the three legacy Crops columns.
            // Values were copied 1:1 into profiles in 2.1 and are verified live, so this Up()
            // loses no data. Down() reverses the 2.1 copy (see below).
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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

            // Reverse of the 2.1 value-copy: repopulate the restored columns from each crop's
            // agronomy profile. GrowthPeriodDays / HarvestWindowDays copy back directly (NULLs
            // stay NULL). PlantingSeason is reconstructed, NOT stored on the profile: the 2.1 copy
            // collapsed legacy 'Year-round' and NULL into all-NULL month columns, with
            // GrowthPeriodDays null-ness as the surviving discriminator (per the Step 2.3
            // bit-identical reconstruction). So: 'Year-round' where all four planting-month
            // columns are NULL AND GrowthPeriodDays IS NOT NULL; otherwise NULL. Yala/Maha are
            // unreachable here (no such rows ever existed) but handled for completeness: any
            // populated month column ⇒ leave PlantingSeason NULL (season not string-encodable).
            migrationBuilder.Sql(@"
UPDATE c
SET c.GrowthPeriodDays  = p.GrowthPeriodDays,
    c.HarvestWindowDays = p.HarvestWindowDays,
    c.PlantingSeason    = CASE
        WHEN p.YalaPlantingStartMonth IS NULL AND p.YalaPlantingEndMonth IS NULL
         AND p.MahaPlantingStartMonth IS NULL AND p.MahaPlantingEndMonth IS NULL
         AND p.GrowthPeriodDays IS NOT NULL
        THEN 'Year-round'
        ELSE NULL
    END
FROM Crops c
INNER JOIN CropAgronomyProfiles p ON p.CropId = c.Id;");
        }
    }
}
