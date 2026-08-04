
namespace BarberShopAPI.ViewModels
{
    public class GetBarberShopClosuresViewModel
    {
        public int Id { get; set; }
        public DateOnly StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        public TimeOnly? StartTime { get; set; }
        public TimeOnly? EndTime { get; set; }
        public string Reason { get; set; }
        public bool IsFullDay { get; set; }
        // Null for shop-wide closures; the barber's name for barber-scoped ones. Lets the admin
        // calendar render "Name - reason" for a just-created closure without waiting for a reload.
        public string? BarberName { get; set; }

        /* True when this closure shuts the whole shop rather than one barber's chair (BarberId == null).
         * The barber's calendar mixes both - his own time off and the days the shop is closed around him -
         * and has to render them differently, which it can't infer from BarberName: that field is only
         * populated for the admin's benefit and is null on the barber's read either way. */
        public bool IsShopWide { get; set; }
    }
}
