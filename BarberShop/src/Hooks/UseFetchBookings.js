import { useState, useContext } from "react";
import { ToastContext } from "../Context/ToastContext.jsx";
import {fetchBookings } from "../utils/FetchBookings.js";

const useFetchBookings = () => {
    const { showToast } = useContext(ToastContext);
    const [loading, setLoading] = useState(false);

    const load = async (params) => {
        console.log("Params are", params);
        setLoading(true);
        try {
            const response = await fetchBookings(params);
            return response;
        }
        catch (err) {
            console.log("err name", err.name);
            console.log("err code", err.code);
            if (err.response?.status === 401) {
                return;
            }
            /* 1. You click logout
2. Logout request fires → token invalidated → you get redirected to /login
3. BUT AdminDashboard's useEffect fires one last time before unmounting
4. admin-fetch request is sent with the now-invalidated token
5. [Authorize] rejects it → 401
6. Interceptor catches the 401 → sees pathname is already "/login" → skips its toast
7. 401 propagates to useFetchBookings catch block
8. No 401 check there → shows "Error getting bookings, An unexpected error occurred" toast
So the toast was showing because useFetchBookings had no idea the 401 was from a logout — it just saw an error and toasted it. 
The interceptor correctly stayed silent but the error kept bubbling up to useFetchBookings which had nothing to stop it from showing the toast. */
            showToast("Error getting bookings", err.response?.data?.message || "An unexpected error occurred");
        }
        finally {
            setLoading(false);
        }
    }
    return { loading, load };
}
export default useFetchBookings;