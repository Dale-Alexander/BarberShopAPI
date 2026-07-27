using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarberShopAPI.Migrations
{
    /// <inheritdoc />
    public partial class SeedBarberSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Back-fill every existing active barber with one open-ended current version. HasData
            // can't enumerate existing rows, so this is raw SQL. EffectiveFrom is a safely-past
            // sentinel so the version covers every date regardless of timezone / midnight edges.
            migrationBuilder.Sql(@"
                INSERT INTO BarberSchedules (BarberId, EffectiveFrom, EffectiveTo)
                SELECT Id, '2020-01-01', NULL FROM Barbers WHERE isActive = 1;");

            // All 7 weekdays 09:00-17:30. The shop is currently implicitly open every day (days off
            // are modelled as closures), so seeding Mon-Fri only would break weekends on day one.
            migrationBuilder.Sql(@"
                INSERT INTO BarberScheduleShifts (BarberScheduleId, DayOfWeek, StartTime, EndTime)
                SELECT s.Id, d.DayNum, '09:00:00', '17:30:00'
                FROM BarberSchedules s
                CROSS JOIN (VALUES (0),(1),(2),(3),(4),(5),(6)) AS d(DayNum)
                WHERE s.EffectiveFrom = '2020-01-01' AND s.EffectiveTo IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE sh FROM BarberScheduleShifts sh
                JOIN BarberSchedules s ON s.Id = sh.BarberScheduleId
                WHERE s.EffectiveFrom = '2020-01-01';");
            migrationBuilder.Sql(@"DELETE FROM BarberSchedules WHERE EffectiveFrom = '2020-01-01';");
        }
    }
}
