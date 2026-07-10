using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class addedISActiveToShopClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            /*migrationBuilder.DropForeignKey(
                name: "FK_ShopClosure_Barbers_BarberId",
                table: "ShopClosures");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ShopClosure",
                table: "ShopClosures");

            migrationBuilder.DropIndex(
                name: "IX_ShopClosure_BarberId",
                table: "ShopClosure");

            migrationBuilder.RenameTable(
                name: "ShopClosure",
                newName: "ShopClosures");*/

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "ShopClosures",
                type: "bit",
                nullable: false,
                defaultValue: true);

            /*migrationBuilder.AddPrimaryKey(
                name: "PK_ShopClosures",
                table: "ShopClosures",
                column: "Id");

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

            migrationBuilder.AddForeignKey(
                name: "FK_ShopClosures_Barbers_BarberId",
                table: "ShopClosures",
                column: "BarberId",
                principalTable: "Barbers",
                principalColumn: "Id");*/
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ShopClosures_Barbers_BarberId",
                table: "ShopClosures");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ShopClosures",
                table: "ShopClosures");

            migrationBuilder.DropIndex(
                name: "IX_ShopClosures_BarberId_Date",
                table: "ShopClosures");

            migrationBuilder.DropIndex(
                name: "IX_ShopClosures_Date",
                table: "ShopClosures");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "ShopClosures");

            migrationBuilder.RenameTable(
                name: "ShopClosures",
                newName: "ShopClosure");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ShopClosure",
                table: "ShopClosure",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_ShopClosure_BarberId",
                table: "ShopClosure",
                column: "BarberId");

            migrationBuilder.AddForeignKey(
                name: "FK_ShopClosure_Barbers_BarberId",
                table: "ShopClosure",
                column: "BarberId",
                principalTable: "Barbers",
                principalColumn: "Id");
        }
    }
}
