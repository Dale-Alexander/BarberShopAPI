import { adminAxios } from "../Hooks/AxiosInterceptor.js";
export const fetchBookings = async ({ fromDate, toDate, status, page = 1, pageSize = 20 }) => {
    const params = new URLSearchParams();
    if (fromDate) params.append("fromDate", fromDate.toISOString());
    if (toDate) params.append("toDate", toDate.toISOString());
    if (status) params.append("status", status);
    params.append("page", page);
    params.append("pageSize", pageSize);
    const res = await adminAxios.get(`/api/bookings/admin-fetch?${params.toString()}`);
    // Shape: { bookings, totalCount, page, pageSize, totalPages }
    return res.data;
}
