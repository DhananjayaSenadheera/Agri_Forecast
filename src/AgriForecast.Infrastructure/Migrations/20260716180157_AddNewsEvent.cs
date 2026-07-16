using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NewsEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "date", nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NewsEventCrops",
                columns: table => new
                {
                    NewsEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CropId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsEventCrops", x => new { x.NewsEventId, x.CropId });
                    table.ForeignKey(
                        name: "FK_NewsEventCrops_Crops_CropId",
                        column: x => x.CropId,
                        principalTable: "Crops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NewsEventCrops_NewsEvents_NewsEventId",
                        column: x => x.NewsEventId,
                        principalTable: "NewsEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NewsEventMarkets",
                columns: table => new
                {
                    NewsEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MarketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsEventMarkets", x => new { x.NewsEventId, x.MarketId });
                    table.ForeignKey(
                        name: "FK_NewsEventMarkets_Markets_MarketId",
                        column: x => x.MarketId,
                        principalTable: "Markets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NewsEventMarkets_NewsEvents_NewsEventId",
                        column: x => x.NewsEventId,
                        principalTable: "NewsEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsEventCrops_CropId",
                table: "NewsEventCrops",
                column: "CropId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsEventMarkets_MarketId",
                table: "NewsEventMarkets",
                column: "MarketId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsEvents_PublishedAt",
                table: "NewsEvents",
                column: "PublishedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NewsEventCrops");

            migrationBuilder.DropTable(
                name: "NewsEventMarkets");

            migrationBuilder.DropTable(
                name: "NewsEvents");
        }
    }
}
