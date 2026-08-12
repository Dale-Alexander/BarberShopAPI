using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddShopSettingsRangeConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_ShopSettings_BufferMin",
                table: "ShopSettings",
                sql: "[BufferMin] BETWEEN 0 AND 120");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ShopSettings_DefaultAdminBookingDurationMin",
                table: "ShopSettings",
                sql: "[DefaultAdminBookingDurationMin] BETWEEN 5 AND 240");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ShopSettings_GraceMinutesAfterClose",
                table: "ShopSettings",
                sql: "[GraceMinutesAfterClose] BETWEEN 0 AND 120");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ShopSettings_MaxAdvanceBookingDays",
                table: "ShopSettings",
                sql: "[MaxAdvanceBookingDays] BETWEEN 1 AND 365");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ShopSettings_MinAdvanceBookingMinutes",
                table: "ShopSettings",
                sql: "[MinAdvanceBookingMinutes] BETWEEN 0 AND 1440");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ShopSettings_RefundCutoffHours",
                table: "ShopSettings",
                sql: "[RefundCutoffHours] BETWEEN 0 AND 168");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ShopSettings_SlotStepMin",
                table: "ShopSettings",
                sql: "[SlotStepMin] IN (5, 10, 15, 20, 30)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ShopSettings_BufferMin",
                table: "ShopSettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ShopSettings_DefaultAdminBookingDurationMin",
                table: "ShopSettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ShopSettings_GraceMinutesAfterClose",
                table: "ShopSettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ShopSettings_MaxAdvanceBookingDays",
                table: "ShopSettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ShopSettings_MinAdvanceBookingMinutes",
                table: "ShopSettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ShopSettings_RefundCutoffHours",
                table: "ShopSettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ShopSettings_SlotStepMin",
                table: "ShopSettings");
        }
    }
}
