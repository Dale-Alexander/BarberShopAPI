using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class NullableStripePaymentIntentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Payment_StripePaymentIntentId",
                table: "Payments");

            migrationBuilder.DropCheckConstraint(
    name: "CK_Payments_Amount_Max400",
    table: "Payments");

            migrationBuilder.AlterColumn<string>(
                name: "StripePaymentIntentId",
                table: "Payments",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "Payments",
                type: "decimal(6,2)",
                nullable: true,
                oldClrType: typeof(float),
                oldType: "real",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Payment_StripePaymentIntentId",
                table: "Payments",
                column: "StripePaymentIntentId",
                unique: true,
                filter: "[StripePaymentIntentId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
    name: "CK_Payments_Amount_Max400",
    table: "Payments",
    sql: "[Amount] <= 400");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Payment_StripePaymentIntentId",
                table: "Payments");

            migrationBuilder.AlterColumn<string>(
                name: "StripePaymentIntentId",
                table: "Payments",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.AlterColumn<float>(
                name: "Amount",
                table: "Payments",
                type: "real",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(6,2)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Payment_StripePaymentIntentId",
                table: "Payments",
                column: "StripePaymentIntentId",
                unique: true);
        }
    }
}
