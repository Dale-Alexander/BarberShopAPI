using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class replacedEnumInShopClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosureType",
                table: "ShopClosure");

            migrationBuilder.AddColumn<bool>(
                name: "IsFullDay",
                table: "ShopClosure",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFullDay",
                table: "ShopClosure");

            migrationBuilder.AddColumn<int>(
                name: "ClosureType",
                table: "ShopClosure",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
