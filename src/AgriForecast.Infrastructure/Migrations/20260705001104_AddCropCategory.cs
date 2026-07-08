using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AgriForecast.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCropCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CropCategoryId",
                table: "Crops",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CropCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CropCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CropCategories_CropCategories_ParentId",
                        column: x => x.ParentId,
                        principalTable: "CropCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "CropCategories",
                columns: new[] { "Id", "Code", "CreatedAt", "Name", "ParentId" },
                values: new object[,]
                {
                    { new Guid("d4c40001-0000-0000-0000-000000000001"), "VEG", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Vegetable", null },
                    { new Guid("d4c40001-0000-0000-0000-000000000002"), "FRT", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Fruit", null },
                    { new Guid("d4c40001-0000-0000-0000-000000000003"), "VEG-UP", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Up-country Vegetable", new Guid("d4c40001-0000-0000-0000-000000000001") },
                    { new Guid("d4c40001-0000-0000-0000-000000000004"), "VEG-LOW", new DateTime(2026, 7, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Low-country Vegetable", new Guid("d4c40001-0000-0000-0000-000000000001") }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Crops_CropCategoryId",
                table: "Crops",
                column: "CropCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CropCategories_Code",
                table: "CropCategories",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CropCategories_ParentId",
                table: "CropCategories",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_Crops_CropCategories_CropCategoryId",
                table: "Crops",
                column: "CropCategoryId",
                principalTable: "CropCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Crops_CropCategories_CropCategoryId",
                table: "Crops");

            migrationBuilder.DropTable(
                name: "CropCategories");

            migrationBuilder.DropIndex(
                name: "IX_Crops_CropCategoryId",
                table: "Crops");

            migrationBuilder.DropColumn(
                name: "CropCategoryId",
                table: "Crops");
        }
    }
}
