using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingPolicySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxAdvanceBookingDays",
                table: "ShopSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinAdvanceBookingMinutes",
                table: "ShopSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RefundCutoffHours",
                table: "ShopSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "ShopSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "MaxAdvanceBookingDays", "MinAdvanceBookingMinutes", "RefundCutoffHours" },
                values: new object[] { 60, 90, 24 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxAdvanceBookingDays",
                table: "ShopSettings");

            migrationBuilder.DropColumn(
                name: "MinAdvanceBookingMinutes",
                table: "ShopSettings");

            migrationBuilder.DropColumn(
                name: "RefundCutoffHours",
                table: "ShopSettings");
        }
    }
}
