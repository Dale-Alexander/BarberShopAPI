namespace BarberShopAPI.Models.Enums
{
    /* Why a booking was cancelled. None is the default (also covers legacy rows and cancellations we
     * don't attribute). The customer-facing cancelled screen and the cancellation emails switch copy on
     * this - e.g. BarberUnavailable tells the customer their barber left and to rebook with another.
     * Stored as int, so only ever APPEND new values - never reorder/insert. ScheduleChange: the barber's
     * working hours changed and the slot no longer fits (distinct from ShopClosure so the customer isn't
     * told the shop was closed). */
    public enum CancellationReason { None, ShopClosure, BarberUnavailable, AdminCancelled, ScheduleChange }
}
