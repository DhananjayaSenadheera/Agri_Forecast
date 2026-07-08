using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCropAgronomyProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CropAgronomyProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CropId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrowthPeriodDays = table.Column<int>(type: "int", nullable: true),
                    HarvestWindowDays = table.Column<int>(type: "int", nullable: true),
                    YalaPlantingStartMonth = table.Column<byte>(type: "tinyint", nullable: true),
                    YalaPlantingEndMonth = table.Column<byte>(type: "tinyint", nullable: true),
                    MahaPlantingStartMonth = table.Column<byte>(type: "tinyint", nullable: true),
                    MahaPlantingEndMonth = table.Column<byte>(type: "tinyint", nullable: true),
                    IsPerennial = table.Column<bool>(type: "bit", nullable: false),
                    DataSource = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    VerifiedOn = table.Column<DateTime>(type: "date", nullable: true),
                    IsVerified = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CropAgronomyProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CropAgronomyProfiles_Crops_CropId",
                        column: x => x.CropId,
                        principalTable: "Crops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CropAgronomyProfiles_CropId",
                table: "CropAgronomyProfiles",
                column: "CropId",
                unique: true);

            // ── VALUE-COPY: create a PENDING agronomy profile for every existing crop. ──
            // Moves GrowthPeriodDays / HarvestWindowDays off Crops (dropped in a later subtask)
            // into a profile row per crop, VALUES UNCHANGED. Both are copied exactly (NULLs stay
            // NULL). PlantingSeason is NOT copied as a string: the legacy column only ever held
            // 'Year-round' or NULL (no Yala/Maha rows), and the year-round / unknown convention is
            // "all four planting-month columns NULL", so every profile lands with month columns NULL
            // and no season signal is lost. Real Yala/Maha months are Step-5 (DOA) curation.
            //   IsPerennial = 0  (perennial verification is Step 5's job)
            //   IsVerified  = 0  (pending — Step 5 curation flips this)
            //   DataSource  = 'legacy-crops-table'
            // NEWID() per row for the profile Id; NOT EXISTS keeps this safe to re-run.
            migrationBuilder.Sql(@"
INSERT INTO CropAgronomyProfiles
    (Id, CropId, GrowthPeriodDays, HarvestWindowDays,
     YalaPlantingStartMonth, YalaPlantingEndMonth, MahaPlantingStartMonth, MahaPlantingEndMonth,
     IsPerennial, DataSource, VerifiedOn, IsVerified, CreatedAt, UpdatedAt)
SELECT
    NEWID(), c.Id, c.GrowthPeriodDays, c.HarvestWindowDays,
    NULL, NULL, NULL, NULL,
    0, 'legacy-crops-table', NULL, 0, SYSUTCDATETIME(), SYSUTCDATETIME()
FROM Crops c
WHERE NOT EXISTS (
    SELECT 1 FROM CropAgronomyProfiles p WHERE p.CropId = c.Id
);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CropAgronomyProfiles");
        }
    }
}
