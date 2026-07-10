import { adminAxios } from "../Hooks/AxiosInterceptor.js";
export const fetchBarberBookings = async ({ fromDate, toDate, status, barberId }) => {
    const params = new URLSearchParams();
    if (fromDate) params.append("fromDate", fromDate.toISOString());
    if (toDate) params.append("toDate", toDate.toISOString());;
    if (status) params.append("status", status);
    const res = await adminAxios.get(`/api/bookings/barber-fetch/${barberId}?${params.toString()}`);
    return res.data;
}