using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgriForecast.Infrastructure.Migrations
{
    /// <summary>
    /// Markets.ShortCode — a short display code (e.g. "DEC", "KEP") shown beside the market name.
    /// Display-only: no FK, no join, nothing in the ML path reads it; Id stays the key and MarketCode the
    /// business key. NOT NULL with a '' default so any row created before a code is chosen is still valid,
    /// and the unique index is filtered to non-empty codes so those blanks cannot collide.
    /// The 12 seeded markets are coded here by their fixed seed Ids (the same rows HasData owns).
    /// Codes are owner-reviewable: renaming one is a HasData edit plus a new migration, and changes
    /// nothing downstream.
    /// </summary>
    public partial class AddMarketShortCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShortCode",
                table: "Markets",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000001"),
                column: "ShortCode",
                value: "DEC");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000002"),
                column: "ShortCode",
                value: "KEP");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000003"),
                column: "ShortCode",
                value: "THB");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000004"),
                column: "ShortCode",
                value: "PET");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000005"),
                column: "ShortCode",
                value: "NAR");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000006"),
                column: "ShortCode",
                value: "NAT");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000007"),
                column: "ShortCode",
                value: "KAN");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000008"),
                column: "ShortCode",
                value: "MEE");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000009"),
                column: "ShortCode",
                value: "NOR");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000010"),
                column: "ShortCode",
                value: "NUW");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000011"),
                column: "ShortCode",
                value: "BAN");

            migrationBuilder.UpdateData(
                table: "Markets",
                keyColumn: "Id",
                keyValue: new Guid("b2a20001-0000-0000-0000-000000000012"),
                column: "ShortCode",
                value: "VEY");

            migrationBuilder.CreateIndex(
                name: "UX_Markets_ShortCode",
                table: "Markets",
                column: "ShortCode",
                unique: true,
                filter: "[ShortCode] <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Markets_ShortCode",
                table: "Markets");

            migrationBuilder.DropColumn(
                name: "ShortCode",
                table: "Markets");
        }
    }
}
