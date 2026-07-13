using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultAdminBookingDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultAdminBookingDurationMin",
                table: "ShopSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "ShopSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "DefaultAdminBookingDurationMin",
                value: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultAdminBookingDurationMin",
                table: "ShopSettings");
        }
    }
}
