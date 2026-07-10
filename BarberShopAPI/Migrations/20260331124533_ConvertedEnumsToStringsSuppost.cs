using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class ConvertedEnumsToStringsSuppost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Payments",
                type: "nvarchar(max)",
                nullable: false,
                defaultValueSql: "'PENDING'",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldDefaultValue: "PENDING");

            migrationBuilder.AlterColumn<string>(
                name: "Method",
                table: "Payments",
                type: "nvarchar(max)",
                nullable: false,
                defaultValueSql: "'CASH'",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldDefaultValue: "CASH");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "BookingServices",
                type: "nvarchar(max)",
                nullable: false,
                defaultValueSql: "'ACTIVE'",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldDefaultValue: "ACTIVE");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Bookings",
                type: "nvarchar(max)",
                nullable: false,
                defaultValueSql: "'PENDING'",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldDefaultValue: "PENDING");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Payments",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "PENDING",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldDefaultValueSql: "'PENDING'");

            migrationBuilder.AlterColumn<string>(
                name: "Method",
                table: "Payments",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "CASH",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldDefaultValueSql: "'CASH'");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "BookingServices",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "ACTIVE",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldDefaultValueSql: "'ACTIVE'");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Bookings",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "PENDING",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldDefaultValueSql: "'PENDING'");
        }
    }
}
