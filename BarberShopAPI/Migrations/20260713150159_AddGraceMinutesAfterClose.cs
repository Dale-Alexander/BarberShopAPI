using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddGraceMinutesAfterClose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GraceMinutesAfterClose",
                table: "ShopSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "ShopSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "GraceMinutesAfterClose",
                value: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GraceMinutesAfterClose",
                table: "ShopSettings");
        }
    }
}
