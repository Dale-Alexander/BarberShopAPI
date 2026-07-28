using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class addEndDateToShopClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            /* Restored alongside the rename repair in 20260315151019: these two indexes are created there,
             * so they must be dropped here before Date becomes StartDate and the composite replacements
             * below are created. They were commented out while that migration was applied by hand. */
            migrationBuilder.DropIndex(
                name: "IX_ShopClosures_BarberId_Date",
                table: "ShopClosures");

            migrationBuilder.DropIndex(
                name: "IX_ShopClosures_Date",
                table: "ShopClosures");

            migrationBuilder.RenameColumn(
                name: "Date",
                table: "ShopClosures",
                newName: "StartDate");

            migrationBuilder.AddColumn<DateOnly>(
                name: "EndDate",
                table: "ShopClosures",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopClosures_BarberId_StartDate_EndDate",
                table: "ShopClosures",
                columns: new[] { "BarberId", "StartDate", "EndDate" },
                unique: true,
                filter: "[BarberId] IS NOT NULL AND [IsFullDay] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ShopClosures_StartDate_EndDate",
                table: "ShopClosures",
                columns: new[] { "StartDate", "EndDate" },
                unique: true,
                filter: "[BarberId] IS NULL AND [IsFullDay] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShopClosures_BarberId_StartDate_EndDate",
                table: "ShopClosures");

            migrationBuilder.DropIndex(
                name: "IX_ShopClosures_StartDate_EndDate",
                table: "ShopClosures");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "ShopClosures");

            migrationBuilder.RenameColumn(
                name: "StartDate",
                table: "ShopClosures",
                newName: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_ShopClosures_BarberId_Date",
                table: "ShopClosures",
                columns: new[] { "BarberId", "Date" },
                unique: true,
                filter: "[BarberId] IS NOT NULL AND [IsFullDay] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ShopClosures_Date",
                table: "ShopClosures",
                column: "Date",
                unique: true,
                filter: "[BarberId] IS NULL AND [IsFullDay] = 1");
        }
    }
}
