using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class FixDateTimeDefaults : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "Users", "Barbers", "Bookings", "Payments" })
            {
                migrationBuilder.AlterColumn<DateTime>(
                    name: "CreatedAt",
                    table: table,
                    type: "datetime2",
                    nullable: false,
                    defaultValueSql: "GETDATE()",
                    oldClrType: typeof(DateTime),
                    oldType: "datetime2");

                migrationBuilder.AlterColumn<DateTime>(
                    name: "UpdatedAt",
                    table: table,
                    type: "datetime2",
                    nullable: false,
                    defaultValueSql: "GETDATE()",
                    oldClrType: typeof(DateTime),
                    oldType: "datetime2");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "Users", "Barbers", "Bookings", "Payments" })
            {
                migrationBuilder.AlterColumn<DateTime>(
                    name: "CreatedAt",
                    table: table,
                    type: "datetime2",
                    nullable: false,
                    oldClrType: typeof(DateTime),
                    oldType: "datetime2",
                    oldDefaultValueSql: "GETDATE()");

                migrationBuilder.AlterColumn<DateTime>(
                    name: "UpdatedAt",
                    table: table,
                    type: "datetime2",
                    nullable: false,
                    oldClrType: typeof(DateTime),
                    oldType: "datetime2",
                    oldDefaultValueSql: "GETDATE()");
            }
        }
    }
}
