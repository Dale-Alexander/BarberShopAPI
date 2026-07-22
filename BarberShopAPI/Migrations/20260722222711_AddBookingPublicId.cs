using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingPublicId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "Bookings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            // Backfill existing rows with a unique 32-char hex slug (matches Guid "N") BEFORE the unique
            // index is created - otherwise every pre-existing row shares the "" default and the index fails.
            migrationBuilder.Sql(
                "UPDATE Bookings SET PublicId = LOWER(REPLACE(CONVERT(varchar(36), NEWID()), '-', '')) WHERE PublicId = '' OR PublicId IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_PublicId",
                table: "Bookings",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bookings_PublicId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "Bookings");
        }
    }
}
