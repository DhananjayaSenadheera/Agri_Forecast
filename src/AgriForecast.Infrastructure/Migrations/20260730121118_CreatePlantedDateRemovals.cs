using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <summary>
    /// The append-only record of every planting date a farmer has CLEARED, with the reason they gave.
    /// <para>
    /// Net-new table, so there is nothing to backfill: rows only exist from the moment the reason became
    /// required, and inventing a reason for a date cleared before that would be fabricating a farmer's answer.
    /// Note is nullable and everything else is required, because a row with no reason is the exact state this
    /// table exists to prevent.
    /// </para>
    /// <para>
    /// Users CASCADEs (personal data does not outlive its owner, as on UserCropWatchlist) and Crops RESTRICTs
    /// (reference data cannot be deleted out from under a record of it). Only one cascade path reaches the
    /// table, so SQL Server accepts both.
    /// </para>
    /// </summary>
    public partial class CreatePlantedDateRemovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlantedDateRemovals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CropId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RemovedPlantedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantedDateRemovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlantedDateRemovals_Crops_CropId",
                        column: x => x.CropId,
                        principalTable: "Crops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlantedDateRemovals_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlantedDateRemovals_CropId",
                table: "PlantedDateRemovals",
                column: "CropId");

            migrationBuilder.CreateIndex(
                name: "IX_PlantedDateRemovals_UserCropOccurredUtc",
                table: "PlantedDateRemovals",
                columns: new[] { "UserId", "CropId", "OccurredUtc" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlantedDateRemovals");
        }
    }
}
