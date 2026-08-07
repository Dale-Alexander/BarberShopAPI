namespace BarberShopAPI.ViewModels
{
    public class BarberShopClosureViewModel
    {
        public int Id { get; set; }
        public bool IsFullDay {get; set;}
        /* Properties, not fields. System.Text.Json ignores public FIELDS by default, so as fields these
         * two were silently dropped from every response that carries this view model - the picker received
         * partial-day closures with no times on them at all and could never tell which hours were shut.
         * Nothing failed loudly: the client's comparison against an undefined time just answered "not
         * closed" every time, so a partial closure looked bookable straight through. */
        public TimeOnly? StartTime { get; set; }
        public TimeOnly? EndTime { get; set; }
        public DateOnly StartDate {get;set;}
        public DateOnly? EndDate { get; set; }
    }
}
