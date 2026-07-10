using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class UpdatedPaymentDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "Price",
                table: "Services",
                type: "decimal(6,2)",
                nullable: false,
                oldClrType: typeof(float),
                oldType: "real");

            migrationBuilder.AlterColumn<int>(
    name: "Method",
    table: "Payments",
    nullable: false,
    defaultValue: 0, // CASH
    oldClrType: typeof(int));

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "Payments",
                nullable: false,
                defaultValue: 0, // PENDING
                oldClrType: typeof(int));

            migrationBuilder.AddCheckConstraint(
    name: "CK_Payments_Amount_Max400",
    table: "Payments",
    sql: "[Amount] <= 400");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<float>(
                name: "Price",
                table: "Services",
                type: "real",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(6,2)");

            migrationBuilder.DropCheckConstraint(
    name: "CK_Payments_Amount_Max400",
    table: "Payments");

            migrationBuilder.AlterColumn<int>(
                name: "Method",
                table: "Payments",
                nullable: false,
                oldClrType: typeof(int),
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "Payments",
                nullable: false,
                oldClrType: typeof(int),
                oldDefaultValue: 0);
        }
    }
}
