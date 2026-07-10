import { useSearchParams } from "react-router-dom";
import { useMemo } from "react";

export const usePersistentFilters = () => {
    const [params, setParams] = useSearchParams();

    // -----------------------------
    // READ FILTERS FROM URL
    // -----------------------------
    const filters = useMemo(() => {
        return {
            status: params.get("status") || null,
            fromDate: params.get("fromDate")
                ? new Date(params.get("fromDate"))
                : null,
            toDate: params.get("toDate")
                ? new Date(params.get("toDate"))
                : null,
        };
    }, [params]);

    // -----------------------------
    // APPLY FILTERS (WRITE TO URL)
    // -----------------------------
    const applyFilters = ({ status, fromDate, toDate }) => {
        const newParams = new URLSearchParams();

        if (status) {
            newParams.set("status", status);
        }

        if (fromDate) {
            newParams.set("fromDate", fromDate.toISOString());
        }

        if (toDate) {
            newParams.set("toDate", toDate.toISOString());
        }

        setParams(newParams);
    };

    // -----------------------------
    // RESET FILTERS
    // -----------------------------
    const resetFilters = () => {
        setParams(new URLSearchParams());
    };

    return {
        filters,
        applyFilters,
        resetFilters,
    };
};