using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecodeCropCodesToCategoryPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // R2 D-DF4: re-code all existing crops to the category-prefixed scheme VEG######/FRT######.
            // Prefix = TOP-LEVEL category (Up-country/Low-country Vegetable sub-categories roll up to
            // VEG via ParentId: COALESCE(parent.Code, category.Code)). Numbering is deterministic and
            // PER-PREFIX via ROW_NUMBER() OVER (PARTITION BY prefix ORDER BY Name, Id) — Id is the
            // tiebreaker because Crops.Name is NOT unique ("Passion" ×2). Assign-once/immutable: the
            // code reflects the category AT ASSIGNMENT and is never re-issued or renumbered afterwards,
            // even if a crop's category later changes.
            //
            // No unique index / FK / join uses CropCode (display-only), and the new VEG/FRT codes do
            // not overlap the old CRO/DMB scheme, so a set-based single-statement UPDATE cannot
            // transiently collide. Runs against every current crop (96 at authoring: 70 VEG / 26 FRT).
            migrationBuilder.Sql(@"
                WITH Recoded AS (
                    SELECT
                        c.Id,
                        CASE WHEN COALESCE(p.Code, cc.Code) = 'FRT' THEN 'FRT' ELSE 'VEG' END AS Prefix,
                        ROW_NUMBER() OVER (
                            PARTITION BY CASE WHEN COALESCE(p.Code, cc.Code) = 'FRT' THEN 'FRT' ELSE 'VEG' END
                            ORDER BY c.Name, c.Id
                        ) AS Rn
                    FROM Crops c
                    INNER JOIN CropCategories cc ON c.CropCategoryId = cc.Id
                    LEFT JOIN CropCategories p ON cc.ParentId = p.Id
                )
                UPDATE c
                SET c.CropCode = r.Prefix + RIGHT('000000' + CAST(r.Rn AS varchar(6)), 6)
                FROM Crops c
                INNER JOIN Recoded r ON c.Id = r.Id;
            ");

            migrationBuilder.RenameColumn(
                name: "Crop_Prefix",
                table: "DefaultSettings",
                newName: "Veg_Prefix");

            migrationBuilder.RenameColumn(
                name: "Crop_Padding",
                table: "DefaultSettings",
                newName: "Veg_Padding");

            migrationBuilder.RenameColumn(
                name: "Crop_Code",
                table: "DefaultSettings",
                newName: "Veg_Code");

            migrationBuilder.AddColumn<int>(
                name: "Frt_Code",
                table: "DefaultSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Frt_Padding",
                table: "DefaultSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Frt_Prefix",
                table: "DefaultSettings",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "DefaultSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "Frt_Code", "Frt_Padding", "Frt_Prefix", "Veg_Code", "Veg_Padding", "Veg_Prefix" },
                values: new object[] { 27, 6, "FRT", 71, 6, "VEG" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ONE-WAY re-code (documented, precedent 20260706162817): Down restores the DefaultSettings
            // code-generator SCHEMA (Veg_*/Frt_* → single Crop_* counter) but CANNOT restore the old
            // per-crop CropCode values (CRO#####/DMB######) — the original codes were overwritten in
            // Up() and no snapshot was captured (they were derived from ExternalProductId / a legacy
            // seed, not recoverable set-based). CropCode is display-only (no index/FK/join), so a Down
            // leaves crops with VEG/FRT codes and a CROP-scheme counter — harmless but not a true
            // reversal. Prefer roll-forward over rolling this back.
            migrationBuilder.DropColumn(
                name: "Frt_Code",
                table: "DefaultSettings");

            migrationBuilder.DropColumn(
                name: "Frt_Padding",
                table: "DefaultSettings");

            migrationBuilder.DropColumn(
                name: "Frt_Prefix",
                table: "DefaultSettings");

            migrationBuilder.RenameColumn(
                name: "Veg_Prefix",
                table: "DefaultSettings",
                newName: "Crop_Prefix");

            migrationBuilder.RenameColumn(
                name: "Veg_Padding",
                table: "DefaultSettings",
                newName: "Crop_Padding");

            migrationBuilder.RenameColumn(
                name: "Veg_Code",
                table: "DefaultSettings",
                newName: "Crop_Code");

            migrationBuilder.UpdateData(
                table: "DefaultSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "Crop_Code", "Crop_Padding", "Crop_Prefix" },
                values: new object[] { 1, 8, "CROP" });
        }
    }
}
