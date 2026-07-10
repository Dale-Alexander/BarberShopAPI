import { useState, useContext } from "react";
import { ToastContext } from "../Context/ToastContext.jsx";
import { fetchBarberBookings } from "../utils/FetchBarberBookings.js";

const useFetchBarberBookings = () => {
    const { showToast } = useContext(ToastContext);
    const [loading, setLoading] = useState(false);

    const load = async (params) => {
        console.log("params are", params);
        setLoading(true);
        try {
            const response = await fetchBarberBookings(params);
            return response;
        }
        catch (err) {
            showToast("Error getting bookings", err.response?.data?.message || "An unexpected error occurred");
        }
        finally {
            setLoading(false);
        }
    }
    return { loading, load };
}
export default useFetchBarberBookings;