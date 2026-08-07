using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddShopHoursSlotStepAndBookingOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SlotStepMin",
                table: "ShopSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "OutsideBarberSchedule",
                table: "Bookings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OutsideShopHours",
                table: "Bookings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "OverriddenByUserId",
                table: "Bookings",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShopHours",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DayOfWeek = table.Column<int>(type: "int", nullable: false),
                    OpenTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    CloseTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    IsClosed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopHours", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "ShopHours",
                columns: new[] { "Id", "CloseTime", "DayOfWeek", "IsClosed", "OpenTime" },
                values: new object[,]
                {
                    { 1, new TimeOnly(17, 30, 0), 0, false, new TimeOnly(9, 0, 0) },
                    { 2, new TimeOnly(17, 30, 0), 1, false, new TimeOnly(9, 0, 0) },
                    { 3, new TimeOnly(17, 30, 0), 2, false, new TimeOnly(9, 0, 0) },
                    { 4, new TimeOnly(17, 30, 0), 3, false, new TimeOnly(9, 0, 0) },
                    { 5, new TimeOnly(17, 30, 0), 4, false, new TimeOnly(9, 0, 0) },
                    { 6, new TimeOnly(17, 30, 0), 5, false, new TimeOnly(9, 0, 0) },
                    { 7, new TimeOnly(17, 30, 0), 6, false, new TimeOnly(9, 0, 0) }
                });

            migrationBuilder.UpdateData(
                table: "ShopSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "SlotStepMin",
                value: 30);

            /* Widen the seeded 09:00-17:30 to cover the shifts this database actually holds.
             *
             * From here on these hours are a ceiling over every barber shift, and the check runs in both
             * directions - a schedule edit is refused if it breaks the hours, and an hours edit is refused
             * if it breaks a schedule. So if any live shift already sits outside the seeded window, the
             * admin opens the schedule editor to a barber they cannot save and no error they caused,
             * fixable only by first widening hours they never set. Seeding from the data instead means the
             * invariant holds the moment it exists, and narrowing is a deliberate act that reports exactly
             * which barbers are in the way.
             *
             * Scoped to current and future versions, matching what the invariant checks - an expired
             * version governs no future date and so can't be violated. Widen-only (the CASEs never narrow),
             * so a day whose shifts are already inside 09:00-17:30 keeps the seeded hours rather than being
             * shrunk to whatever happens to be rostered. */
            migrationBuilder.Sql(@"
                UPDATE h
                SET h.OpenTime  = CASE WHEN a.MinStart < h.OpenTime  THEN a.MinStart ELSE h.OpenTime  END,
                    h.CloseTime = CASE WHEN a.MaxEnd   > h.CloseTime THEN a.MaxEnd   ELSE h.CloseTime END
                FROM ShopHours h
                INNER JOIN (
                    SELECT sh.DayOfWeek, MIN(sh.StartTime) AS MinStart, MAX(sh.EndTime) AS MaxEnd
                    FROM BarberScheduleShifts sh
                    INNER JOIN BarberSchedules s ON s.Id = sh.BarberScheduleId
                    WHERE s.EffectiveTo IS NULL OR s.EffectiveTo >= CAST(GETDATE() AS date)
                    GROUP BY sh.DayOfWeek
                ) a ON a.DayOfWeek = h.DayOfWeek;");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_OverriddenByUserId",
                table: "Bookings",
                column: "OverriddenByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopHours_DayOfWeek",
                table: "ShopHours",
                column: "DayOfWeek",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_Users_OverriddenByUserId",
                table: "Bookings",
                column: "OverriddenByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_Users_OverriddenByUserId",
                table: "Bookings");

            migrationBuilder.DropTable(
                name: "ShopHours");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_OverriddenByUserId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "SlotStepMin",
                table: "ShopSettings");

            migrationBuilder.DropColumn(
                name: "OutsideBarberSchedule",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OutsideShopHours",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OverriddenByUserId",
                table: "Bookings");
        }
    }
}
