using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddDataIntegrityCheckConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            /* Hand-added: CK_Payments_Amount supersedes this, but the scaffolder could not know that. The old
             * constraint was declared straight onto MigrationBuilder in 20260214122638 and never mirrored in
             * OnModelCreating, so it is absent from the model snapshot and the diff neither sees nor drops it.
             * Without this the table would carry both - the old top-only bound sitting redundantly beside the
             * new one. The replacement is model-declared, so it will not drift the same way. */
            migrationBuilder.DropCheckConstraint(
                name: "CK_Payments_Amount_Max400",
                table: "Payments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ShopHours_OpenBeforeClose",
                table: "ShopHours",
                sql: "[IsClosed] = 1 OR [OpenTime] < [CloseTime]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Payments_Amount",
                table: "Payments",
                sql: "[Amount] IS NULL OR ([Amount] > 0 AND [Amount] <= 400)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BarberScheduleShifts_DayOfWeek",
                table: "BarberScheduleShifts",
                sql: "[DayOfWeek] BETWEEN 0 AND 6");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BarberScheduleShifts_StartBeforeEnd",
                table: "BarberScheduleShifts",
                sql: "[StartTime] < [EndTime]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ShopHours_OpenBeforeClose",
                table: "ShopHours");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Payments_Amount",
                table: "Payments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BarberScheduleShifts_DayOfWeek",
                table: "BarberScheduleShifts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BarberScheduleShifts_StartBeforeEnd",
                table: "BarberScheduleShifts");

            // Restores what Up dropped, so this migration is a true inverse.
            migrationBuilder.AddCheckConstraint(
                name: "CK_Payments_Amount_Max400",
                table: "Payments",
                sql: "[Amount] <= 400");
        }
    }
}
