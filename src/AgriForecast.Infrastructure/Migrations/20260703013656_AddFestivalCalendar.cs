using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFestivalCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FestivalCalendarEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FestivalKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    LeadUpDays = table.Column<int>(type: "int", nullable: false),
                    IsProvisional = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FestivalCalendarEntries", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "FestivalCalendarEntries",
                columns: new[] { "Id", "CreatedAtUtc", "Date", "FestivalKey", "IsProvisional", "LeadUpDays", "Source" },
                values: new object[,]
                {
                    { new Guid("c3f30001-0000-0000-7014-000000002015"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2015, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2015" },
                    { new Guid("c3f30001-0000-0000-7014-000000002016"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2016, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2016" },
                    { new Guid("c3f30001-0000-0000-7014-000000002017"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2017, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2017" },
                    { new Guid("c3f30001-0000-0000-7014-000000002018"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2018, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2018" },
                    { new Guid("c3f30001-0000-0000-7014-000000002019"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2019, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2019" },
                    { new Guid("c3f30001-0000-0000-7014-000000002020"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2020, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2020" },
                    { new Guid("c3f30001-0000-0000-7014-000000002021"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2021, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2021" },
                    { new Guid("c3f30001-0000-0000-7014-000000002022"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2022, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2022" },
                    { new Guid("c3f30001-0000-0000-7014-000000002023"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2023, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2023" },
                    { new Guid("c3f30001-0000-0000-7014-000000002024"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2024" },
                    { new Guid("c3f30001-0000-0000-7014-000000002025"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2025, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2025" },
                    { new Guid("c3f30001-0000-0000-7014-000000002026"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2026" },
                    { new Guid("c3f30001-0000-0000-7014-000000002027"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", true, 14, null },
                    { new Guid("c3f30001-0000-0000-7014-000000002028"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2028, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", true, 14, null },
                    { new Guid("c3f30001-0000-0000-7014-000000002029"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2029, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", true, 14, null },
                    { new Guid("c3f30001-0000-0000-7014-000000002030"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2030, 1, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "THAI_PONGAL", true, 14, null },
                    { new Guid("c3f30001-0000-0000-a013-000000002015"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2015, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2015" },
                    { new Guid("c3f30001-0000-0000-a013-000000002016"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2016, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2016" },
                    { new Guid("c3f30001-0000-0000-a013-000000002017"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2017, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2017" },
                    { new Guid("c3f30001-0000-0000-a013-000000002018"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2018, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2018" },
                    { new Guid("c3f30001-0000-0000-a013-000000002019"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2019, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2019" },
                    { new Guid("c3f30001-0000-0000-a013-000000002020"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2020, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2020" },
                    { new Guid("c3f30001-0000-0000-a013-000000002021"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2021, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2021" },
                    { new Guid("c3f30001-0000-0000-a013-000000002022"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2022, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2022" },
                    { new Guid("c3f30001-0000-0000-a013-000000002023"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2023, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2023" },
                    { new Guid("c3f30001-0000-0000-a013-000000002024"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2024" },
                    { new Guid("c3f30001-0000-0000-a013-000000002025"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2025, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2025" },
                    { new Guid("c3f30001-0000-0000-a013-000000002026"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 14, "Department of Government Printing, Sri Lanka — annual holiday gazette 2026" },
                    { new Guid("c3f30001-0000-0000-a013-000000002027"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", true, 14, null },
                    { new Guid("c3f30001-0000-0000-a013-000000002028"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2028, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", true, 14, null },
                    { new Guid("c3f30001-0000-0000-a013-000000002029"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2029, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", true, 14, null },
                    { new Guid("c3f30001-0000-0000-a013-000000002030"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2030, 4, 13, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", true, 14, null },
                    { new Guid("c3f30001-0000-0000-a014-000000002015"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2015, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2015" },
                    { new Guid("c3f30001-0000-0000-a014-000000002016"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2016, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2016" },
                    { new Guid("c3f30001-0000-0000-a014-000000002017"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2017, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2017" },
                    { new Guid("c3f30001-0000-0000-a014-000000002018"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2018, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2018" },
                    { new Guid("c3f30001-0000-0000-a014-000000002019"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2019, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2019" },
                    { new Guid("c3f30001-0000-0000-a014-000000002020"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2020, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2020" },
                    { new Guid("c3f30001-0000-0000-a014-000000002021"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2021, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2021" },
                    { new Guid("c3f30001-0000-0000-a014-000000002022"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2022, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2022" },
                    { new Guid("c3f30001-0000-0000-a014-000000002023"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2023, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2023" },
                    { new Guid("c3f30001-0000-0000-a014-000000002024"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2024" },
                    { new Guid("c3f30001-0000-0000-a014-000000002025"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2025, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2025" },
                    { new Guid("c3f30001-0000-0000-a014-000000002026"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", false, 0, "Department of Government Printing, Sri Lanka — annual holiday gazette 2026" },
                    { new Guid("c3f30001-0000-0000-a014-000000002027"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", true, 0, null },
                    { new Guid("c3f30001-0000-0000-a014-000000002028"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2028, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", true, 0, null },
                    { new Guid("c3f30001-0000-0000-a014-000000002029"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2029, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", true, 0, null },
                    { new Guid("c3f30001-0000-0000-a014-000000002030"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2030, 4, 14, 0, 0, 0, 0, DateTimeKind.Unspecified), "AVURUDU", true, 0, null },
                    { new Guid("c3f30001-0000-0000-c025-000000002015"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2015, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002016"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2016, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002017"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2017, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002018"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2018, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002019"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2019, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002020"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2020, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002021"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2021, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002022"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2022, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002023"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2023, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002024"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002025"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2025, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002026"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002027"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002028"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2028, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002029"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2029, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" },
                    { new Guid("c3f30001-0000-0000-c025-000000002030"), new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(2030, 12, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), "CHRISTMAS", false, 14, "Fixed Gregorian date (Dec 25)" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_FestivalCalendarEntries_Date",
                table: "FestivalCalendarEntries",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_FestivalCalendarEntries_FestivalKey_Date",
                table: "FestivalCalendarEntries",
                columns: new[] { "FestivalKey", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FestivalCalendarEntries");
        }
    }
}
