using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiMarketAndPointInTimeData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MarketId",
                table: "EconomicCenters",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Mkt_Code",
                table: "DefaultSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Mkt_Padding",
                table: "DefaultSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mkt_Prefix",
                table: "DefaultSettings",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Markets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MarketCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    District = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MarketType = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Markets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MarketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CropId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalCommodityId = table.Column<int>(type: "int", nullable: true),
                    ExternalCommodityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ObservedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    WholesalePrice = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    RetailPrice = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    MinPrice = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    MaxPrice = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    ArrivalsKg = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    AsOfUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceObservations_Crops_CropId",
                        column: x => x.CropId,
                        principalTable: "Crops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PriceObservations_Markets_MarketId",
                        column: x => x.MarketId,
                        principalTable: "Markets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "DefaultSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "Mkt_Code", "Mkt_Padding", "Mkt_Prefix" },
                values: new object[] { 7, 8, "MKT" });

            migrationBuilder.InsertData(
                table: "Markets",
                columns: new[] { "Id", "CreatedAt", "District", "IsActive", "MarketCode", "MarketType", "Name", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("b2a20001-0000-0000-0000-000000000001"), new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc), "Matale", true, "MKT00000001", 2, "Dambulla Dedicated Economic Centre", new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("b2a20001-0000-0000-0000-000000000002"), new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc), "Badulla", true, "MKT00000002", 2, "Keppetipola Dedicated Economic Centre", new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("b2a20001-0000-0000-0000-000000000003"), new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc), "Anuradhapura", true, "MKT00000003", 2, "Thambuttegama Dedicated Economic Centre", new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("b2a20001-0000-0000-0000-000000000004"), new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc), "Colombo", true, "MKT00000004", 0, "Pettah (HARTI wholesale)", new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("b2a20001-0000-0000-0000-000000000005"), new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc), "Colombo", true, "MKT00000005", 1, "Narahenpita (HARTI retail)", new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("b2a20001-0000-0000-0000-000000000006"), new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "MKT00000006", 3, "CBSL national average (pseudo-market)", new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_EconomicCenters_MarketId",
                table: "EconomicCenters",
                column: "MarketId");

            migrationBuilder.CreateIndex(
                name: "IX_Markets_MarketCode",
                table: "Markets",
                column: "MarketCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceObservations_CropId",
                table: "PriceObservations",
                column: "CropId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceObservations_MarketCropDate",
                table: "PriceObservations",
                columns: new[] { "MarketId", "CropId", "ObservedDate" });

            migrationBuilder.CreateIndex(
                name: "UX_PriceObservations_MarketCommodityIdDateSource",
                table: "PriceObservations",
                columns: new[] { "MarketId", "ExternalCommodityId", "ObservedDate", "Source" },
                unique: true,
                filter: "[ExternalCommodityId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_PriceObservations_MarketCommodityNameDateSource",
                table: "PriceObservations",
                columns: new[] { "MarketId", "ExternalCommodityName", "ObservedDate", "Source" },
                unique: true,
                filter: "[ExternalCommodityId] IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_EconomicCenters_Markets_MarketId",
                table: "EconomicCenters",
                column: "MarketId",
                principalTable: "Markets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // --- Back-compat data mapping: EconomicCenter rows -> Markets ---------------
            // Every existing EconomicCenter gets a Market twin so the promoted dimension
            // is complete without discarding or re-keying legacy rows. The twin is a
            // Wholesale market (DECs and legacy centres are wholesale trading hubs),
            // carries only the centre's Name, and a data-derived MarketCode
            // 'ECOMAP-' + <EconomicCenter.Id> that cannot collide with the MKT###### seed
            // codes. EconomicCenter.MarketId is then pointed at its twin.
            //
            // Keyed on the PRIMARY KEY (ec.Id), not EcoCode: EcoCode is nullable and
            // runtime-assigned (legacy rows may have NULL), and 'ECOMAP-' + NULL = NULL
            // would violate the NOT NULL / unique MarketCode and make the NOT EXISTS guard
            // NULL-blind. ec.Id is guaranteed non-null, stable, and unique -> a collision-proof,
            // idempotent key.
            //
            // District is deliberately LEFT NULL: EconomicCenter.Location is free text and
            // District is a controlled dimension — do not pollute it with unvalidated strings.
            //
            // Set-based and idempotent: only maps centres not already mapped, and only
            // creates twins that don't already exist (keyed on the derived MarketCode),
            // so a re-run is a no-op. NEWID() is fine for the twin's own PK — these are
            // back-fill rows, not part of the deterministic seed.
            migrationBuilder.Sql(@"
INSERT INTO [Markets] ([Id], [MarketCode], [Name], [District], [MarketType], [IsActive], [CreatedAt], [UpdatedAt])
SELECT NEWID(), 'ECOMAP-' + CONVERT(nvarchar(36), ec.[Id]), ec.[Name], NULL, 0 /* Wholesale */, 1,
       SYSUTCDATETIME(), SYSUTCDATETIME()
FROM [EconomicCenters] ec
WHERE ec.[MarketId] IS NULL
  AND NOT EXISTS (SELECT 1 FROM [Markets] m WHERE m.[MarketCode] = 'ECOMAP-' + CONVERT(nvarchar(36), ec.[Id]));

UPDATE ec
SET ec.[MarketId] = m.[Id]
FROM [EconomicCenters] ec
INNER JOIN [Markets] m ON m.[MarketCode] = 'ECOMAP-' + CONVERT(nvarchar(36), ec.[Id])
WHERE ec.[MarketId] IS NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the Up back-fill BEFORE any drops so EconomicCenters return to their
            // exact pre-migration state. Note: a down-migration for a table-add inherently
            // drops the Markets and PriceObservations tables and ANY user data that landed
            // in them after the Up — that is what "down" means here and is unavoidable. What
            // we explicitly undo is the back-fill's side effects on the pre-existing
            // EconomicCenters table: null out the MarketId we set, and delete only the
            // ECOMAP-% twin markets this migration created (never user/seeded markets).
            // Nulling MarketId first also releases the Restrict FK so the twin DELETE and the
            // subsequent Markets DropTable cannot be blocked by a dangling reference.
            migrationBuilder.Sql(@"
UPDATE [EconomicCenters] SET [MarketId] = NULL WHERE [MarketId] IS NOT NULL;
DELETE FROM [Markets] WHERE [MarketCode] LIKE 'ECOMAP-%';
");

            migrationBuilder.DropForeignKey(
                name: "FK_EconomicCenters_Markets_MarketId",
                table: "EconomicCenters");

            migrationBuilder.DropTable(
                name: "PriceObservations");

            migrationBuilder.DropTable(
                name: "Markets");

            migrationBuilder.DropIndex(
                name: "IX_EconomicCenters_MarketId",
                table: "EconomicCenters");

            migrationBuilder.DropColumn(
                name: "MarketId",
                table: "EconomicCenters");

            migrationBuilder.DropColumn(
                name: "Mkt_Code",
                table: "DefaultSettings");

            migrationBuilder.DropColumn(
                name: "Mkt_Padding",
                table: "DefaultSettings");

            migrationBuilder.DropColumn(
                name: "Mkt_Prefix",
                table: "DefaultSettings");
        }
    }
}
