using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class FilterUniqueIndexToActiveClosures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShopClosures_BarberId_StartDate_EndDate",
                table: "ShopClosures");

            migrationBuilder.DropIndex(
                name: "IX_ShopClosures_StartDate_EndDate",
                table: "ShopClosures");

            migrationBuilder.CreateIndex(
                name: "IX_ShopClosures_BarberId_StartDate_EndDate",
                table: "ShopClosures",
                columns: new[] { "BarberId", "StartDate", "EndDate" },
                unique: true,
                filter: "[BarberId] IS NOT NULL AND [IsFullDay] = 1 AND [IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ShopClosures_StartDate_EndDate",
                table: "ShopClosures",
                columns: new[] { "StartDate", "EndDate" },
                unique: true,
                filter: "[BarberId] IS NULL AND [IsFullDay] = 1 AND [IsActive] = 1");
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
    }
}
