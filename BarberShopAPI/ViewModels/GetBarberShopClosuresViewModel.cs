
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
    }
}
