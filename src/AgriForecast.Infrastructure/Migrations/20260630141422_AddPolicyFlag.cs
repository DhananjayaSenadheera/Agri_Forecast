using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPolicyFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PolicyFlags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyType = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "date", nullable: true),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReferenceUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyFlags", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "PolicyFlags",
                columns: new[] { "Id", "CreatedAtUtc", "Description", "Direction", "EffectiveFrom", "EffectiveTo", "PolicyType", "ReferenceUrl", "Source", "Title" },
                values: new object[,]
                {
                    { new Guid("a1f1c001-0000-0000-0000-000000000001"), new DateTime(2026, 6, 30, 0, 0, 0, 0, DateTimeKind.Utc), "Government banned imports of chemical fertilisers, pesticides and weedicides, forcing a nationwide shift to organic farming. Cut yields sharply across paddy and vegetables, pushing harvest-time prices up.", 1, new DateTime(2021, 5, 6, 0, 0, 0, 0, DateTimeKind.Unspecified), new DateTime(2021, 11, 24, 0, 0, 0, 0, DateTimeKind.Unspecified), 1, "https://en.wikipedia.org/wiki/2021%E2%80%932022_Sri_Lankan_political_crisis", "Government of Sri Lanka", "Chemical fertiliser & agrochemical import ban" },
                    { new Guid("a1f1c001-0000-0000-0000-000000000002"), new DateTime(2026, 6, 30, 0, 0, 0, 0, DateTimeKind.Utc), "Reinstated fertiliser support for the 2022/23 Maha season via direct cash and subsidised fertiliser to paddy farmers, easing input costs and partially recovering yields.", -1, new DateTime(2022, 10, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new DateTime(2023, 3, 31, 0, 0, 0, 0, DateTimeKind.Unspecified), 5, null, "Ministry of Agriculture, Sri Lanka", "Aswesuma / fertiliser cash subsidy for paddy farmers" },
                    { new Guid("a1f1c001-0000-0000-0000-000000000003"), new DateTime(2026, 6, 30, 0, 0, 0, 0, DateTimeKind.Utc), "Introduction of a transparent monthly fuel pricing formula. Transport/diesel cost feeds into farm-gate to wholesale transport margins; ongoing, still in effect.", 0, new DateTime(2022, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), null, 6, null, "Ceylon Petroleum Corporation", "Monthly fuel price formula (CPC pricing formula)" },
                    { new Guid("a1f1c001-0000-0000-0000-000000000004"), new DateTime(2026, 6, 30, 0, 0, 0, 0, DateTimeKind.Utc), "Import controls / suspension on big onions and potatoes to protect local growers around the harvest window, tightening domestic supply and lifting prices.", 1, new DateTime(2020, 7, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new DateTime(2021, 2, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), 1, null, "Department of Imports and Exports Control, Sri Lanka", "Big onion & potato import restrictions" },
                    { new Guid("a1f1c001-0000-0000-0000-000000000005"), new DateTime(2026, 6, 30, 0, 0, 0, 0, DateTimeKind.Utc), "Consumer Affairs Authority imposed maximum retail prices (price ceilings) on Nadu, Samba and Keeri Samba rice to curb retail inflation during the economic crisis.", -1, new DateTime(2023, 2, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), new DateTime(2024, 1, 31, 0, 0, 0, 0, DateTimeKind.Unspecified), 3, null, "Consumer Affairs Authority, Sri Lanka", "Maximum retail price on rice varieties" },
                    { new Guid("a1f1c001-0000-0000-0000-000000000006"), new DateTime(2026, 6, 30, 0, 0, 0, 0, DateTimeKind.Utc), "Continued subsidised fertiliser distribution to paddy farmers for the 2023/24 Maha season, supporting normalised yields; still in effect.", -1, new DateTime(2023, 10, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), null, 5, null, "Ministry of Agriculture, Sri Lanka", "Fertiliser subsidy scheme continuation (2023/24)" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyFlags_EffectiveFrom",
                table: "PolicyFlags",
                column: "EffectiveFrom");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PolicyFlags");
        }
    }
}
