using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <summary>
    /// The farmer's own sales log: one row per sale they typed in themselves.
    /// <para>
    /// QUARANTINED (PRD 3.1). This is the one table holding farmer-entered PRICES and it is a dead end for
    /// them — nothing copies a row into PriceObservations or MarketPrices, no view or computed column
    /// exposes PricePerKg to the feature layer, and the Python loader is statically forbidden from naming
    /// the table. A farmer's own price training the model that then advises that farmer is a feedback loop
    /// dressed up as data.
    /// </para>
    /// <para>
    /// Net-new table, so there is nothing to backfill: a sales log starts empty because nobody had anywhere
    /// to record a sale before it existed, and inventing rows would be fabricating a farmer's history.
    /// </para>
    /// <para>
    /// Users CASCADEs (personal data does not outlive its owner, as on UserCropWatchlist and
    /// PlantedDateRemovals) while Crops and Markets RESTRICT (reference data cannot be deleted out from
    /// under a farmer's record of it). Only one cascade path reaches the table, so SQL Server accepts all
    /// three. MarketId is nullable — "I sold it, I am not saying where" is a normal answer — and so are
    /// QuantityKg and Note; everything that makes the row a record (who, what crop, when, how much per kilo)
    /// is NOT NULL.
    /// </para>
    /// </summary>
    public partial class CreateUserSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserSales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CropId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MarketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SaleDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PricePerKg = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    QuantityKg = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSales_Crops_CropId",
                        column: x => x.CropId,
                        principalTable: "Crops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserSales_Markets_MarketId",
                        column: x => x.MarketId,
                        principalTable: "Markets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserSales_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSales_CropId",
                table: "UserSales",
                column: "CropId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSales_MarketId",
                table: "UserSales",
                column: "MarketId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSales_UserSaleDate",
                table: "UserSales",
                columns: new[] { "UserId", "SaleDate" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSales");
        }
    }
}
