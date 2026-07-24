using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddCancellationReasonToBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CancellationReason",
                table: "Bookings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "Bookings");
        }
    }
}
