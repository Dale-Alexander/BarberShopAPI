import { useSearchParams } from "react-router-dom";
import { useMemo } from "react";

export const usePersistentFilters = () => {
    const [params, setParams] = useSearchParams();

    // -----------------------------
    // READ FILTERS FROM URL
    // -----------------------------
    /* Two independent axes, not one `status`: bookingStatus is the lifecycle (CONFIRMED / CANCELLED / ALL,
     * null meaning the CONFIRMED default) and paymentStatus is PAID / UNPAID (null meaning any). They used
     * to share a single param, which made the "All" pill mean "any payment status, confirmed only" and hid
     * every cancellation. needsReview is a separate cross-cutting worklist that ignores both. */
    const filters = useMemo(() => {
        return {
            bookingStatus: params.get("bookingStatus") || null,
            paymentStatus: params.get("paymentStatus") || null,
            needsReview: params.get("needsReview") === "true",
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
    const applyFilters = ({ bookingStatus, paymentStatus, needsReview, fromDate, toDate }) => {
        const newParams = new URLSearchParams();

        if (bookingStatus) {
            newParams.set("bookingStatus", bookingStatus);
        }

        if (paymentStatus) {
            newParams.set("paymentStatus", paymentStatus);
        }

        if (needsReview) {
            newParams.set("needsReview", "true");
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