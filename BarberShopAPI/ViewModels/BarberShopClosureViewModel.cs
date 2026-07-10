namespace BarberShopAPI.ViewModels
{
    public class BarberShopClosureViewModel
    {
        public int Id { get; set; }
        public bool IsFullDay {get; set;}
        public TimeOnly? StartTime;
        public TimeOnly? EndTime;
        public DateOnly StartDate {get;set;}
        public DateOnly? EndDate { get; set; }
    }
}
