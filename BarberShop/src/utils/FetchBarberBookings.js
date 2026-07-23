import { adminAxios } from "../Hooks/AxiosInterceptor.js";
export const fetchBarberBookings = async ({ fromDate, toDate, bookingStatus, paymentStatus, barberId, page = 1, pageSize = 20 }) => {
    const params = new URLSearchParams();
    if (fromDate) params.append("fromDate", fromDate.toISOString());
    if (toDate) params.append("toDate", toDate.toISOString());
    // Omitted params take the server's defaults: CONFIRMED bookings, any payment status.
    if (bookingStatus) params.append("bookingStatus", bookingStatus);
    if (paymentStatus) params.append("paymentStatus", paymentStatus);
    params.append("page", page);
    params.append("pageSize", pageSize);
    const res = await adminAxios.get(`/api/bookings/barber-fetch/${barberId}?${params.toString()}`);
    // Shape: { barberName, barberSurname, barberImageUrl, bookings, totalCount, page, pageSize, totalPages }
    return res.data;
}
