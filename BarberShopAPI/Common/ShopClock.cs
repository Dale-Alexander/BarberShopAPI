using System;

namespace BarberShopAPI.Common
{
    // Booking.StartDateTime is a shop-local (Europe/Malta) wall-clock value with no
    // offset/Kind info - a 2:30pm appointment is 2:30pm regardless of what timezone
    // the hosting server's OS happens to be set to. This helper is the single place
    // that knows how to translate between that Malta-local world and true UTC, so
    // business-logic comparisons stay correct no matter where the app is deployed.
    public static class ShopClock
    {
        private static readonly TimeZoneInfo MaltaTimeZone = ResolveMaltaTimeZone();

        private static TimeZoneInfo ResolveMaltaTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Europe/Malta");
            }
            catch (TimeZoneNotFoundException)
            {
                // Fallback for environments without ICU/IANA data (e.g. InvariantGlobalization).
                return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
            }
        }

        // Current wall-clock time in Malta, for comparing against StartDateTime.
        public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, MaltaTimeZone);

        // Today's date in Malta, for DateOnly-based closure filtering.
        public static DateOnly Today => DateOnly.FromDateTime(Now);

        // Current time-of-day in Malta, for TimeOnly-based closure filtering.
        public static TimeOnly TimeOfDay => TimeOnly.FromDateTime(Now);

        // Converts a Malta-local wall-clock DateTime (e.g. StartDateTime, or
        // StartDateTime.AddHours(-2)) into a true UTC instant - needed anywhere a
        // TimeSpan delay must be computed against DateTime.UtcNow (Hangfire schedules).
        //
        // Comparisons are safe with plain ShopClock.Now (DST doesn't affect ordering -
        // May 1st is always later than April 1st). Subtraction is not: naive DateTime
        // subtraction on Unspecified-Kind values assumes "1 wall-clock hour = 1 real
        // hour," which is false across the two nights a year Malta's clocks jump. If
        // "now" and the target time fall on opposite sides of a DST transition, that
        // naive subtraction is off by exactly the DST offset (1 hour) - converting both
        // sides to true UTC first avoids that.
        public static DateTime ToUtc(DateTime maltaLocalTime) =>
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(maltaLocalTime, DateTimeKind.Unspecified), MaltaTimeZone);

        // Converts a UTC DateTime (e.g. a UTC-Z query-string bound date) into
        // Malta-local wall-clock time, for comparison against StartDateTime. Used by
        // the admin/barber booking-list date-range filters, which receive proper UTC
        // (.toISOString()) from the frontend but need to compare against StartDateTime
        // (Malta-local).
        public static DateTime FromUtc(DateTime utcTime) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc), MaltaTimeZone);
    }
}
