import { adminAxios } from "../Hooks/AxiosInterceptor.js";
// Returns { totalBookings, distinctClients, distinctServices } over the same filter as barber-fetch.
export const fetchBarberSummary = async ({ barberId, fromDate, toDate, bookingStatus, paymentStatus } = {}) => {
    const params = new URLSearchParams();
    if (fromDate) params.append("fromDate", fromDate.toISOString());
    if (toDate) params.append("toDate", toDate.toISOString());
    if (bookingStatus) params.append("bookingStatus", bookingStatus);
    if (paymentStatus) params.append("paymentStatus", paymentStatus);
    const res = await adminAxios.get(`/api/bookings/barber-summary/${barberId}?${params.toString()}`);
    return res.data;
}
