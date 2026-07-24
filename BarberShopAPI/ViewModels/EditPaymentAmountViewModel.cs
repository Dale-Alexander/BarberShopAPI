namespace BarberShopAPI.ViewModels
{
    /* Body for PATCH edit-amount: correcting the collected amount on an already-paid cash booking (e.g. a
     * discount given on the day, or services that differed from what was booked) so the revenue chart and
     * stat cards reflect what actually landed in the till. Required, unlike mark-cash-paid's optional
     * amount - here the admin is deliberately setting a value. Bounds enforced in the controller. */
    public class EditPaymentAmountViewModel
    {
        public decimal Amount { get; set; }
    }
}
