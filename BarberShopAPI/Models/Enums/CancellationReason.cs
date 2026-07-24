namespace BarberShopAPI.Models.Enums
{
    /* Why a booking was cancelled. None is the default (also covers legacy rows and cancellations we
     * don't attribute). The customer-facing cancelled screen and the cancellation emails switch copy on
     * this - e.g. BarberUnavailable tells the customer their barber left and to rebook with another. */
    public enum CancellationReason { None, ShopClosure, BarberUnavailable, AdminCancelled }
}
