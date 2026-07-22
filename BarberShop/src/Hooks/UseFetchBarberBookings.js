import { useState, useContext } from "react";
import { ToastContext } from "../Context/ToastContext.jsx";
import { fetchBarberBookings } from "../utils/FetchBarberBookings.js";
import { getErrorMessage } from "../utils/errorMessage.js";

const useFetchBarberBookings = () => {
    const { showToast } = useContext(ToastContext);
    const [loading, setLoading] = useState(false);
    // Exposed so the page can tell a genuine 404 (barber deleted) apart from a failed load, and show a
    // Retry instead of a misleading "Barber Not Found".
    const [error, setError] = useState(null);

    const load = async (params) => {
        setLoading(true);
        setError(null);
        try {
            const response = await fetchBarberBookings(params);
            return response;
        }
        catch (err) {
            if (err.response?.status !== 401) {
                setError(err);
                showToast("Couldn't load bookings", getErrorMessage(err));
            }
        }
        finally {
            setLoading(false);
        }
    }
    return { loading, load, error };
}
export default useFetchBarberBookings;