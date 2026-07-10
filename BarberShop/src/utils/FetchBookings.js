import { adminAxios } from "../Hooks/AxiosInterceptor.js";
export const fetchBookings = async ({ fromDate, toDate, status }) => {
    console.log("fromDate", fromDate, "tDate", toDate);
    const params = new URLSearchParams();
    if (fromDate) params.append("fromDate", fromDate.toISOString());
    if (toDate) params.append("toDate", toDate.toISOString());
    if (status) params.append("status", status);
    console.log("Params is", params);
    const res = await adminAxios.get(`/api/bookings/admin-fetch?${params.toString()}`);
    return res.data;
}