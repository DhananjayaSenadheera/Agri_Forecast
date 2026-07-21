using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CreateIngestionRunsAndVerifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IngestionRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CoveredFromDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CoveredToDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RowsFetched = table.Column<int>(type: "int", nullable: true),
                    RowsInserted = table.Column<int>(type: "int", nullable: true),
                    RowsSkipped = table.Column<int>(type: "int", nullable: true),
                    DistinctCrops = table.Column<int>(type: "int", nullable: true),
                    ErrorSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngestionVerifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PipelineDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RunUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OverallStatus = table.Column<int>(type: "int", nullable: false),
                    NChecksPass = table.Column<int>(type: "int", nullable: false),
                    NChecksWarn = table.Column<int>(type: "int", nullable: false),
                    NChecksFail = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ChecksJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionVerifications", x => x.Id);
                    table.CheckConstraint("CK_IngestionVerifications_ChecksJson_IsJson", "ISJSON([ChecksJson]) = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_IngestionRuns_BatchId",
                table: "IngestionRuns",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionRuns_SourceStartedUtc",
                table: "IngestionRuns",
                columns: new[] { "Source", "StartedUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_IngestionVerifications_BatchId",
                table: "IngestionVerifications",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionVerifications_RunUtc",
                table: "IngestionVerifications",
                column: "RunUtc",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngestionRuns");

            migrationBuilder.DropTable(
                name: "IngestionVerifications");
        }
    }
}
