import { adminAxios } from "../Hooks/AxiosInterceptor.js";
export const fetchBookings = async ({ fromDate, toDate, bookingStatus, paymentStatus, needsReview, page = 1, pageSize = 20 }) => {
    const params = new URLSearchParams();
    if (fromDate) params.append("fromDate", fromDate.toISOString());
    if (toDate) params.append("toDate", toDate.toISOString());
    // Omitted params take the server's defaults: CONFIRMED bookings, any payment status.
    if (bookingStatus) params.append("bookingStatus", bookingStatus);
    if (paymentStatus) params.append("paymentStatus", paymentStatus);
    if (needsReview) params.append("needsReview", "true");
    params.append("page", page);
    params.append("pageSize", pageSize);
    const res = await adminAxios.get(`/api/bookings/admin-fetch?${params.toString()}`);
    // Shape: { bookings, totalCount, page, pageSize, totalPages }
    return res.data;
}
