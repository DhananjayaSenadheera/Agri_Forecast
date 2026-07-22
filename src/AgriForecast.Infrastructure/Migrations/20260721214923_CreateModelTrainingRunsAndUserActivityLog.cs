using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CreateModelTrainingRunsAndUserActivityLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModelTrainingRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TrainedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Promoted = table.Column<bool>(type: "bit", nullable: false),
                    DecisionPromoted = table.Column<bool>(type: "bit", nullable: false),
                    PromotionDecision = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    BestMlKind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BestMlMae = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    BestBaselineKind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BestBaselineMae = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    NTrainRows = table.Column<int>(type: "int", nullable: true),
                    NCrops = table.Column<int>(type: "int", nullable: true),
                    FeatureContractHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelTrainingRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserActivityLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UsernameAttempted = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserActivityLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelTrainingRuns_TrainedAtUtc",
                table: "ModelTrainingRuns",
                column: "TrainedAtUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_ModelTrainingRuns_Version",
                table: "ModelTrainingRuns",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserActivityLog_OccurredUtc",
                table: "UserActivityLog",
                column: "OccurredUtc",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelTrainingRuns");

            migrationBuilder.DropTable(
                name: "UserActivityLog");
        }
    }
}
