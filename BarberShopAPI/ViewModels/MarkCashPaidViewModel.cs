namespace BarberShopAPI.ViewModels
{
    /* Body for PATCH mark-cash-paid. Amount is optional: customer cash bookings already carry the
     * amount from their chosen services and don't send one, while admin (phone) bookings have no
     * amount on file until the customer pays in person, so it's captured here - or left blank if the
     * admin is reconciling later and doesn't remember. */
    public class MarkCashPaidViewModel
    {
        public decimal? Amount { get; set; }
    }
}
