using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <summary>
    /// Per-crop watched markets replace the one-home-market-per-farmer rule, and each watched crop gains an
    /// optional planting date.
    /// <para>
    /// THE ORDER OF Up() IS LOAD-BEARING. The scaffolder put the DropColumn first; it has been moved
    /// AFTER the data copy, because dropping PreferredMarketId before reading it would silently throw away
    /// every market a farmer had already chosen. The sequence is: create the child table, add PlantedDate,
    /// COPY each existing home market into a child row, and only then drop the old column and its FK/index.
    /// </para>
    /// <para>
    /// The copy is a no-op on an empty table and cannot duplicate: one source row can produce at most one
    /// child row (PreferredMarketId is a single nullable column), so it satisfies
    /// UX_UserCropWatchMarkets_EntryMarket by construction. CreatedAtUtc is carried over from the parent
    /// rather than stamped with SYSUTCDATETIME(), so the migrated market keeps its real "chosen at" time
    /// and stays first in the oldest-chosen display order.
    /// </para>
    /// </summary>
    public partial class PerCropWatchMarketsAndPlantedDate : Migration
    {
        /// <summary>
        /// The data migration itself, kept as a named constant so a test can assert that this migration
        /// carries it and that it runs before the column it reads is dropped.
        /// </summary>
        public const string CopyPreferredMarketsSql =
            """
            INSERT INTO [UserCropWatchMarkets] ([Id], [UserCropWatchlistId], [MarketId], [CreatedAtUtc])
            SELECT NEWID(), [Id], [PreferredMarketId], [CreatedAtUtc]
            FROM [UserCropWatchlist]
            WHERE [PreferredMarketId] IS NOT NULL;
            """;

        /// <summary>
        /// The reverse copy: the FIRST market of each entry becomes the single home market again. Anything
        /// beyond the first is lost, which is inherent to going back to a one-market column and is why
        /// Down() is a recovery path, not a round trip.
        /// </summary>
        private const string RestorePreferredMarketsSql =
            """
            UPDATE w
            SET w.[PreferredMarketId] = x.[MarketId]
            FROM [UserCropWatchlist] w
            CROSS APPLY (
                SELECT TOP 1 m.[MarketId]
                FROM [UserCropWatchMarkets] m
                WHERE m.[UserCropWatchlistId] = w.[Id]
                ORDER BY m.[CreatedAtUtc], m.[MarketId]
            ) x;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserCropWatchMarkets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserCropWatchlistId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MarketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCropWatchMarkets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserCropWatchMarkets_Markets_MarketId",
                        column: x => x.MarketId,
                        principalTable: "Markets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserCropWatchMarkets_UserCropWatchlist_UserCropWatchlistId",
                        column: x => x.UserCropWatchlistId,
                        principalTable: "UserCropWatchlist",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserCropWatchMarkets_MarketId",
                table: "UserCropWatchMarkets",
                column: "MarketId");

            migrationBuilder.CreateIndex(
                name: "UX_UserCropWatchMarkets_EntryMarket",
                table: "UserCropWatchMarkets",
                columns: new[] { "UserCropWatchlistId", "MarketId" },
                unique: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PlantedDate",
                table: "UserCropWatchlist",
                type: "date",
                nullable: true);

            // Carry every already-chosen home market over BEFORE the column that holds it disappears.
            migrationBuilder.Sql(CopyPreferredMarketsSql);

            migrationBuilder.DropForeignKey(
                name: "FK_UserCropWatchlist_Markets_PreferredMarketId",
                table: "UserCropWatchlist");

            migrationBuilder.DropIndex(
                name: "IX_UserCropWatchlist_PreferredMarketId",
                table: "UserCropWatchlist");

            migrationBuilder.DropColumn(
                name: "PreferredMarketId",
                table: "UserCropWatchlist");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PreferredMarketId",
                table: "UserCropWatchlist",
                type: "uniqueidentifier",
                nullable: true);

            // Restore what a single-market column can hold, again BEFORE the source of the data is dropped.
            migrationBuilder.Sql(RestorePreferredMarketsSql);

            migrationBuilder.CreateIndex(
                name: "IX_UserCropWatchlist_PreferredMarketId",
                table: "UserCropWatchlist",
                column: "PreferredMarketId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserCropWatchlist_Markets_PreferredMarketId",
                table: "UserCropWatchlist",
                column: "PreferredMarketId",
                principalTable: "Markets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropColumn(
                name: "PlantedDate",
                table: "UserCropWatchlist");

            migrationBuilder.DropTable(
                name: "UserCropWatchMarkets");
        }
    }
}
