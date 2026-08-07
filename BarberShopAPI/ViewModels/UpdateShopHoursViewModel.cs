using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    /* One weekday's opening hours. The PUT takes all seven at once (see UpdateShopHoursRequest) rather
     * than one day at a time, because the barber-shift check they have to pass is a whole-week question:
     * saving Monday and Tuesday separately would reject a pair of edits that is perfectly valid together. */
    public class ShopHoursDayViewModel : IValidatableObject
    {
        [Range(0, 6, ErrorMessage = "Day of week must be between 0 (Sunday) and 6 (Saturday)")]
        public int DayOfWeek { get; set; }

        public TimeOnly OpenTime { get; set; }
        public TimeOnly CloseTime { get; set; }

        public bool IsClosed { get; set; }

        /* Open >= close is meaningless and would silently produce a day with no bookable minute in it.
         * Skipped for a closed day, whose times are ignored entirely and kept only so reopening restores
         * what was there before - validating them would block closing a day whose stored times are junk. */
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (!IsClosed && OpenTime >= CloseTime)
                yield return new ValidationResult(
                    "Opening time must be before closing time",
                    new[] { nameof(OpenTime) });
        }
    }

    public class UpdateShopHoursRequest : IValidatableObject
    {
        public List<ShopHoursDayViewModel> Days { get; set; } = new();

        /* Exactly seven distinct weekdays. The rows are seeded and never created or deleted through the
         * API, so a payload that is missing Wednesday or sends Monday twice is a malformed request, not a
         * partial update to be merged - taking it as one would leave a day silently unchanged while the
         * response reported success. */
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Days.Count != 7)
                yield return new ValidationResult("All seven days must be provided", new[] { nameof(Days) });
            else if (Days.Select(d => d.DayOfWeek).Distinct().Count() != 7)
                yield return new ValidationResult("Each weekday must appear exactly once", new[] { nameof(Days) });
        }
    }

    /* A barber shift that would fall outside the proposed hours, returned with the 400 so the admin is told
     * WHO to fix rather than just that something is wrong. Req 2: they adjust the schedules first, then the
     * hours change goes through. */
    public class ConflictingShiftViewModel
    {
        public int BarberId { get; set; }
        public string? BarberName { get; set; }
        public string Day { get; set; } = "";
        public string Shift { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
