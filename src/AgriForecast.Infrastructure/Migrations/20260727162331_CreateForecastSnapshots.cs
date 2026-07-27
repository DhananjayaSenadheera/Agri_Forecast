using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CreateForecastSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ForecastSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CropId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnapshotDate = table.Column<DateOnly>(type: "date", nullable: false),
                    HarvestDate = table.Column<DateOnly>(type: "date", nullable: true),
                    GrowthPeriodDays = table.Column<int>(type: "int", nullable: true),
                    PredictedPrice = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    LowerBound = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    UpperBound = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    ReferencePrice = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    Confidence = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ActivePredictor = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FallbackTier = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModelVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MaturityState = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActualPrice = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    ActualObservedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SignedError = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    AbsoluteError = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    PercentageError = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    WithinInterval = table.Column<bool>(type: "bit", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    MaturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastSnapshots", x => x.Id);
                    table.CheckConstraint("CK_ForecastSnapshots_Band", "[UpperBound] >= [LowerBound] AND [LowerBound] >= 0");
                    table.CheckConstraint("CK_ForecastSnapshots_MaturityState", "[MaturityState] COLLATE Latin1_General_BIN2 IN ('pending', 'matured', 'actual_unavailable', 'not_maturable')");
                    table.ForeignKey(
                        name: "FK_ForecastSnapshots_Crops_CropId",
                        column: x => x.CropId,
                        principalTable: "Crops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastSnapshots_CropSnapshotDateDesc",
                table: "ForecastSnapshots",
                columns: new[] { "CropId", "SnapshotDate" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastSnapshots_HarvestDatePending",
                table: "ForecastSnapshots",
                column: "HarvestDate",
                filter: "[MaturityState] = 'pending'");

            migrationBuilder.CreateIndex(
                name: "UX_ForecastSnapshots_CropSnapshotDate",
                table: "ForecastSnapshots",
                columns: new[] { "CropId", "SnapshotDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ForecastSnapshots");
        }
    }
}
