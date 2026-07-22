using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CreateSystemErrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemErrors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExceptionType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StackTrace = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Path = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    TraceId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemErrors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SystemErrors_OccurredUtc",
                table: "SystemErrors",
                column: "OccurredUtc",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SystemErrors");
        }
    }
}
