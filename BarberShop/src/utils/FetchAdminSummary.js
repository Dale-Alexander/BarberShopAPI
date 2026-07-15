import { adminAxios } from "../Hooks/AxiosInterceptor.js";
// month is 1-based (or null for the yearly view). Returns { stats, availableYears, year, monthly, daily }.
export const fetchAdminSummary = async ({ year, month } = {}) => {
    const params = new URLSearchParams();
    if (year != null) params.append("year", year);
    if (month != null) params.append("month", month);
    const res = await adminAxios.get(`/api/bookings/admin-summary?${params.toString()}`);
    return res.data;
}
