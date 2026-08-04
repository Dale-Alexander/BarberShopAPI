import { useState, useEffect, useContext, Fragment } from "react";
import { useNavigate, useLocation } from "react-router-dom";
import { SquarePen, Trash2, Plus, Filter, Search, AlertTriangle, CheckCircle2, Banknote, Coins } from "lucide-react";
import { format } from "date-fns";
import FilterModal from "../Filter/Filter.jsx";
import "./BookingsTable.css";
import { adminAxios } from "../../../../Hooks/AxiosInterceptor";
import { ToastContext } from "../../../../Context/ToastContext.jsx";
import Pagination from "../../../../Components/Pagination/Pagination.jsx";
import LoadingSpinner from "../../../../Components/LoadingSpinner/LoadingSpinner.jsx";
import ErrorState from "../../../../Components/ErrorState/ErrorState.jsx";
import { getErrorMessage } from "../../../../utils/errorMessage.js";
import { formatPhone } from "../../../../utils/phone.js";
import { formatEuro } from "../../../../utils/money.js";
import useFetch from "../../../../Hooks/useFetch.js";

const getMaltaNow = () =>
    new Date(new Date().toLocaleString("en-US", { timeZone: "Europe/Malta" }));

/* Where the "you just dealt with this row" marks live between page loads. sessionStorage rather than
   localStorage: these are meant to expire, just not as abruptly as a refresh. Both helpers swallow
   failures because storage throws outright in some private-browsing modes, and a lost marker must never
   take the bookings table down with it. */
const TOUCHED_STORAGE_KEY = "bookingsTable.recentlyTouched";

const readStoredTouched = () => {
    try {
        const raw = sessionStorage.getItem(TOUCHED_STORAGE_KEY);
        const parsed = raw ? JSON.parse(raw) : null;
        // Guard the shape too - a hand-edited or half-written value would otherwise crash every render.
        return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : {};
    } catch {
        return {};
    }
};

const writeStoredTouched = (map) => {
    try {
        sessionStorage.setItem(TOUCHED_STORAGE_KEY, JSON.stringify(map));
    } catch {
        /* over quota or storage disabled - the marks just won't survive the next load */
    }
};

const BookingsTable = ({ bookings,setBookings, resetFilters, applyFilters, filters, needsReviewCount = 0, refreshNeedsReviewCount, refreshSummary, page = 1, totalPages = 1, onPageChange, loading = false, error = false, onRetry }) => {
    const [openMenuId, setOpenMenuId] = useState(null);
    /* The action menu is positioned fixed (viewport-relative) so it escapes the table's
     * overflow-x:auto wrapper, which otherwise clips it on narrow screens / for the last row. */
    const [menuStyle, setMenuStyle] = useState(null);
    const [searchInput, setSearchInput] = useState("");
    const [filterModalOpen, setFilterModalOpen] = useState(false);
    const [cancelTarget, setCancelTarget] = useState(null);
    const [refundAnyway, setRefundAnyway] = useState(false);
    const [reviewTarget, setReviewTarget] = useState(null);
    const [markPaidTarget, setMarkPaidTarget] = useState(null);
    const [markPaidAmount, setMarkPaidAmount] = useState("");
    // Correcting the collected amount on an already-paid cash booking (discount / different services on
    // the day) so the revenue chart reflects reality. Prefilled with the current amount.
    const [editAmountTarget, setEditAmountTarget] = useState(null);
    const [editAmountValue, setEditAmountValue] = useState("");
    /* Rows the admin has acted on, as { bookingId: "edited" | "cancelled" }. Nothing clears the
       needs-review flag automatically (see UpdateBooking's note), so after dealing with a booking they
       still have to mark it reviewed - this is what tells them WHICH row that is, instead of a second
       pass over a list where every row looks identical to the one they just fixed.

       Two buckets, because they expire differently:
         recentlyTouched - made during this visit. Never pruned, so the booking you just cancelled stays
                           marked for as long as you're on the page even though it has no flag to sign off.
         restoredTouched - carried over from a previous visit (sessionStorage). Pruned down to the rows
                           that are STILL flagged, so what survives a refresh is only what you still owe
                           a sign-off on. A finished booking comes back clean rather than nagging forever.

       Browser-local on purpose: "just edited" is a note to yourself about what you did, and would be a
       lie sitting on the screen of another admin who didn't do it. Per browser session, so it also dies
       when they close the tab. */
    const [recentlyTouched, setRecentlyTouched] = useState({});
    const [restoredTouched, setRestoredTouched] = useState(readStoredTouched);
    const { showToast } = useContext(ToastContext);
    const navigate = useNavigate();
    const location = useLocation();

    /* Editing happens on a different page (/datetime/:id), which navigates back here on save and hands
       the id over in history state. Consume it once and strip it, so a refresh doesn't re-announce an
       edit the admin has already dealt with. The filters live in the query string, so keep `search`. */
    useEffect(() => {
        const editedId = location.state?.recentlyEditedBookingId;
        if (editedId == null) return;
        setRecentlyTouched(prev => ({ ...prev, [editedId]: "edited" }));
        navigate(`${location.pathname}${location.search}`, { replace: true, state: null });
    }, [location.state, location.pathname, location.search, navigate]);

    /* Drop a carried-over mark as soon as we can SEE that its booking no longer needs signing off. Only
       ever against rows actually on screen - the list is filtered and paged, so a mark whose booking
       isn't in view says nothing about whether it's still flagged and must be left alone. Deliberately
       not applied to recentlyTouched, which would wipe this visit's marks the instant they're made. */
    useEffect(() => {
        if (!bookings?.length) return;
        setRestoredTouched(prev => {
            const settled = Object.keys(prev).filter(id =>
                bookings.some(b => String(b.id) === id && !b.needsReview));
            if (settled.length === 0) return prev;
            const next = { ...prev };
            settled.forEach(id => delete next[id]);
            return next;
        });
    }, [bookings]);

    // One merged map is what gets persisted, so a mark made this visit survives the trip out to the edit
    // page (which unmounts this table) as well as an outright refresh.
    useEffect(() => {
        writeStoredTouched({ ...restoredTouched, ...recentlyTouched });
    }, [restoredTouched, recentlyTouched]);

    const touchedFor = (bookingId) => recentlyTouched[bookingId] ?? restoredTouched[bookingId];

    const markTouched = (bookingId, how) =>
        setRecentlyTouched(prev => ({ ...prev, [bookingId]: how }));

    // Signing the booking off is what retires the marker - leaving it would keep drawing the eye to a row
    // with nothing left to do on it. Both buckets, since the mark may have come from either.
    const clearTouched = (bookingId) => {
        const drop = (prev) => {
            if (!(bookingId in prev)) return prev;
            const next = { ...prev };
            delete next[bookingId];
            return next;
        };
        setRecentlyTouched(drop);
        setRestoredTouched(drop);
    };

    /* Cancelling within this many hours of the appointment forfeits the customer's refund - the shop's
     * configured cutoff (ShopSettings), mirrors the backend RefundCutoffHours in BookingCanceller. Falls
     * back to 24 until loaded / on failure, matching the prior hard-coded default. */
    const { data: shopSettings } = useFetch("/api/Settings", true);
    const refundCutoffHours = shopSettings?.refundCutoffHours ?? 24;

    /* startDateTime is Malta wall-clock (parsed as local) and getMaltaNow() is Malta's clock as local, so
     * this difference is a true Malta-vs-Malta comparison regardless of the admin's own timezone. */
    const isWithinRefundCutoff = (booking) => {
        const diffHours = (new Date(booking.startDateTime) - getMaltaNow()) / (1000 * 60 * 60);
        return diffHours < refundCutoffHours;
    };

    /* A booking that has already passed can't be rescheduled or cancelled - the backend rejects both
     * (UpdateBooking / GetBooking guard on ShopClock.Now), so we grey the actions out instead of letting
     * the admin click through to an error. Malta-vs-Malta comparison, same as isWithinRefundCutoff. */
    const isPast = (booking) => new Date(booking.startDateTime) < getMaltaNow();

    /* A cancelled booking is terminal: the backend rejects edit, cancel and mark-as-paid on it
     * (UpdateBooking / CancelBooking / MarkCashPaid all guard on Status), so the row offers no actions
     * rather than letting the admin click through to an error toast. These rows only appear at all under
     * the Cancelled / All booking-status filters. */
    const isCancelled = (booking) => booking.status === "CANCELLED";

    /* Whether the row has any action left to offer. A cancelled booking keeps only "Mark reviewed", so one
     * that isn't flagged has an empty menu - show a dash instead of a button that opens nothing. */
    const hasActions = (booking) => !isCancelled(booking) || booking.needsReview;

    useEffect(() => {
        if (openMenuId == null) return;
        const close = () => setOpenMenuId(null);
        document.addEventListener("click", close);
        /* The menu is viewport-fixed, so a scroll/resize would leave it floating away from its
         * button - close it instead of letting it detach. Capture phase catches inner scrollers too. */
        window.addEventListener("scroll", close, true);
        window.addEventListener("resize", close);
        return () => {
            document.removeEventListener("click", close);
            window.removeEventListener("scroll", close, true);
            window.removeEventListener("resize", close);
        };
    }, [openMenuId])

    /* Toggle the row action menu, pinning it to the viewport just under (or above) the button.
     * Right-aligned to the button and clamped to an 8px gutter so it never spills off-screen. */
    const MENU_WIDTH = 150;
    const toggleActionMenu = (e, bookingId) => {
        e.stopPropagation();
        if (openMenuId === bookingId) {
            setOpenMenuId(null);
            return;
        }
        const rect = e.currentTarget.getBoundingClientRect();
        const spaceBelow = window.innerHeight - rect.bottom;
        const openUp = spaceBelow < 200 && rect.top > spaceBelow;
        const left = Math.max(8, Math.min(rect.right - MENU_WIDTH, window.innerWidth - MENU_WIDTH - 8));
        setMenuStyle({
            position: "fixed",
            left: `${left}px`,
            ...(openUp
                ? { bottom: `${window.innerHeight - rect.top + 6}px` }
                : { top: `${rect.bottom + 6}px` }),
        });
        setOpenMenuId(bookingId);
    };

    const onCancel = async (bookingId, forceRefund = false) => {
        try {
            const res = await adminAxios.patch(`/api/bookings/cancel/${bookingId}${forceRefund ? "?refundAnyway=true" : ""}`);
            /* The row only stops belonging in the (default) confirmed-only view. Under All - or in the
             * needs-review worklist, which a cancelled booking stays flagged in - keep it and flip its
             * status so it re-badges as Cancelled instead of vanishing. */
            const rowStillBelongs = filters.needsReview || filters.bookingStatus === "ALL";
            setBookings(prev =>
                rowStillBelongs
                    ? prev.map(b => b.id === bookingId ? { ...b, status: "CANCELLED" } : b)
                    : prev.filter(b => b.id !== bookingId)
            );
            // Pull the chart/stat cards back down so the now-cancelled booking leaves the COMPLETED-only
            // series immediately, instead of lingering until the next page load.
            refreshSummary?.();
            markTouched(bookingId, "cancelled");
            showToast("Booking cancelled", res.data?.message || "The booking was cancelled.", "success");
        }
        catch (err) {
            /* Surface the server's reason (e.g. a failed Stripe refund on a 502) instead of only logging it -
             * otherwise the admin sees the modal close with the booking still listed and no explanation. */
            showToast("Cancellation failed", getErrorMessage(err));
        }
    }

    const confirmCancel = () => {
        if (!cancelTarget) return;
        /* refundAnyway only matters inside the cutoff; outside it a full refund happens automatically. */
        const force = isWithinRefundCutoff(cancelTarget) && refundAnyway;
        onCancel(cancelTarget.id, force);
        setCancelTarget(null);
    }

    /* Needs-review isn't a value on either filter axis - it's a cross-cutting worklist that ignores both
     * (a flagged booking can be any status). Clicking the header button switches the table in/out of that
     * view via the same URL-param filter plumbing, clearing the other filters on the way in. */
    const toggleNeedsReview = () => {
        applyFilters({
            needsReview: !filters.needsReview,
            bookingStatus: null,
            paymentStatus: null,
            fromDate: null,
            toDate: null,
        });
    }

    const onMarkReviewed = async (bookingId) => {
        try {
            await adminAxios.patch(`/api/bookings/mark-reviewed/${bookingId}`);
            /* In the review view the row no longer belongs, so drop it; in any other view keep the row but
             * clear the flag so the warning marker disappears. Either way, refresh the badge count. */
            setBookings(prev =>
                filters.needsReview
                    ? prev.filter(b => b.id !== bookingId)
                    : prev.map(b => b.id === bookingId ? { ...b, needsReview: false, reviewReason: null } : b)
            );
            refreshNeedsReviewCount?.();
            clearTouched(bookingId);
            showToast("Marked as reviewed", `Booking #${bookingId} was cleared from the review list.`, "success");
        }
        catch (err) {
            showToast("Couldn't mark as reviewed", getErrorMessage(err));
        }
    }

    const confirmReview = () => {
        if (!reviewTarget) return;
        onMarkReviewed(reviewTarget.id);
        setReviewTarget(null);
    }

    /* Cash bookings sit as COMPLETED booking + PENDING payment until the money is collected in person.
     * This flips the payment to COMPLETED. `amount` is only sent for admin (phone) bookings, which have
     * no amount on file; customer cash bookings keep their stored amount and pass null. */
    const onMarkPaid = async (bookingId, amount) => {
        try {
            const res = await adminAxios.patch(`/api/bookings/mark-cash-paid/${bookingId}`, amount != null ? { amount } : {});
            /* In the Unpaid view the row no longer belongs, so drop it; in any other view keep
             * it but flip the status and reflect any amount the admin just entered. Mirrors mark-reviewed. */
            setBookings(prev =>
                filters.paymentStatus === "UNPAID"
                    ? prev.filter(b => b.id !== bookingId)
                    : prev.map(b => b.id === bookingId
                        ? { ...b, paymentStatus: "COMPLETED", amount: res.data?.amount ?? b.amount }
                        : b)
            );
            // A phone booking has no amount on file until it's marked paid, so collecting it adds that
            // revenue to the chart's COMPLETED series. Refresh so the chart/cards reflect it immediately.
            refreshSummary?.();
            showToast("Marked as paid", res.data?.message || `Booking #${bookingId} was marked as collected.`, "success");
        }
        catch (err) {
            showToast("Couldn't mark as paid", getErrorMessage(err));
        }
    }

    /* Amount is optional here (admin may not remember); blank means "collected, amount unknown". When
     * given, it must mirror the backend bounds. The error is shown inline under the field (and disables
     * the button), so a bad value never reaches confirmMarkPaid. */
    const markPaidAmountError = (() => {
        const trimmed = markPaidAmount.trim();
        if (trimmed === "") return null;
        const parsed = Number(trimmed);
        if (!Number.isFinite(parsed) || parsed <= 0 || parsed > 400)
            return "Enter an amount greater than 0 and at most 400, or leave it blank.";
        return null;
    })();

    const confirmMarkPaid = () => {
        if (!markPaidTarget || markPaidAmountError) return;
        const trimmed = markPaidAmount.trim();
        const amount = trimmed === "" ? null : Number(trimmed);
        onMarkPaid(markPaidTarget.id, amount);
        setMarkPaidTarget(null);
    }

    /* Corrects the collected amount on an already-paid cash booking. Reflects in the row and, via
     * refreshSummary, the chart/stat cards (which sum COMPLETED payments). */
    const onEditAmount = async (bookingId, amount) => {
        try {
            const res = await adminAxios.patch(`/api/bookings/edit-amount/${bookingId}`, { amount });
            setBookings(prev => prev.map(b => b.id === bookingId ? { ...b, amount: res.data?.amount ?? amount } : b));
            refreshSummary?.();
            showToast("Amount updated", res.data?.message || `Booking #${bookingId} amount updated.`, "success");
        }
        catch (err) {
            showToast("Couldn't update amount", getErrorMessage(err));
        }
    }

    // Unlike mark-paid, the amount is required here - the admin is deliberately setting a value. Mirrors the
    // backend bounds; the error disables the button so a bad value never reaches confirmEditAmount.
    const editAmountError = (() => {
        const trimmed = editAmountValue.trim();
        if (trimmed === "") return "Enter an amount greater than 0 and at most 400.";
        const parsed = Number(trimmed);
        if (!Number.isFinite(parsed) || parsed <= 0 || parsed > 400)
            return "Enter an amount greater than 0 and at most 400.";
        return null;
    })();

    const confirmEditAmount = () => {
        if (!editAmountTarget || editAmountError) return;
        onEditAmount(editAmountTarget.id, Number(editAmountValue.trim()));
        setEditAmountTarget(null);
    }

    return (
        <div className="bookings-admin-table-container">
            <div className="bookings-admin-table-header">
                <h2 className="bookings-admin-table-title">Recent Bookings</h2>
                <div className="bookings-admin-table-actions">

                    <div className="search-wrapper">
                        <div className="search-icon">
                            <Search size={16}/>
                        </div>
                        <input
                            type="text"
                            placeholder="Search bookings..."
                            className="bookings-admin-table-search-input"
                            value={searchInput}
                            onChange={(e) => setSearchInput(e.target.value)}
                        />
                    </div>

                    {needsReviewCount > 0 && (
                        <section aria-label="Needs review">
                            <button
                                onClick={toggleNeedsReview}
                                title="Bookings whose money couldn't be reconciled automatically - check Stripe and reconcile by hand"
                                className={`bookings-admin-table-btn bookings-admin-table-btn-review ${filters.needsReview ? "review-active" : ""}`}
                            >
                                <span className="bookings-admin-table-btn-icon"><AlertTriangle size={16} /></span>
                                <span className="btn-label">Needs Review</span>
                                <span className="review-badge">{needsReviewCount}</span>
                            </button>
                        </section>
                    )}

                    <section aria-label="Filter">
                        <button onClick={() => setFilterModalOpen(true)}  className="bookings-admin-table-btn bookings-admin-table-btn-filter">
                            <span className="bookings-admin-table-btn-icon"><Filter size={16} /></span>
                            Filter
                        </button>
                    </section>

                    <section aria-label="Create Booking" onClick={() => navigate("/datetime?adminBooking=true") }>
                        <button className="bookings-admin-table-btn bookings-admin-table-btn-create">
                            <span className="bookings-admin-table-btn-icon"><Plus size={16}/></span>
                            Create Booking
                        </button>
                    </section>

                </div>
            </div>
            {/* First load has no rows yet, so take over the table area with a spinner. A page/filter
                refetch keeps the current rows visible under a subtle busy overlay (see the overlay
                below) rather than blanking data the admin is already looking at. */}
            {error && bookings.length === 0 ? (
                <ErrorState inline title="Couldn't load bookings" message="We couldn't load your bookings. Please try again." onRetry={onRetry} />
            ) : loading && bookings.length === 0 ? (
                <LoadingSpinner message="Loading Bookings" color="#e0e0e0" inline />
            ) : (
            <div style={{ position: "relative", overflowX: "auto" }}>
                <table className="bookings-table">
                    <thead>
                        <tr>
                            <th className="table-header">Booking ID</th>
                            <th className="table-header">Customer</th>
                            <th className="table-header">Barber</th>
                            <th className="table-header">Date & Time</th>
                            {/* Hidden on tablet by class rather than :nth-child - the position of this
                                column has already shifted once and a positional selector silently starts
                                hiding whatever moves into slot 4. */}
                            <th className="table-header col-amount">Amount</th>
                            <th className="table-header">Status</th>
                            <th className="table-header">Payment</th>
                            <th className="table-header">Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        {bookings.map((b) => {
                            const touched = touchedFor(b.id);
                            return (
                            <Fragment key={b.id}>
                            <tr className={touched ? "row-recently-touched" : undefined} style={{ transition: "background 0.2s" }}>
                                <td className="table-data" data-label="Booking ID">
                                    <span className="booking-id-cell">
                                        {b.id}
                                        {/* Says what the admin did, not what the booking needs - the amber
                                            flag/reason row below is what carries "something is still owed
                                            here". Two different jobs, so two different colours. */}
                                        {touched && (
                                            <span className="recent-touch-pill">
                                                {touched === "cancelled" ? "Just cancelled" : "Just edited"}
                                            </span>
                                        )}
                                        {/* The tooltip has to hang off this span, not the icon. Lucide spreads
                                            unknown props straight onto its <svg>, and an SVG element ignores a
                                            `title` ATTRIBUTE - it only shows a tooltip for a <title> CHILD
                                            element. Put it on the icon and the reason silently never appears. */}
                                        {b.needsReview && (
                                            <span
                                                className="needs-review-flag-wrap"
                                                title={b.reviewReason || "Needs manual review"}
                                            >
                                                <AlertTriangle
                                                    size={14}
                                                    className="needs-review-flag"
                                                    aria-label="Needs review"
                                                />
                                            </span>
                                        )}
                                    </span>
                                </td>
                                {/* Phone sits under the name rather than in its own column: it's the same
                                    person, and a flagged row whose instruction is "call them" is useless
                                    without it. Walk-ins booked by staff may have no phone on file. */}
                                <td className="table-data" data-label="Customer">
                                    <span className="customer-cell">
                                        <span>{b.firstName}</span>
                                        {b.phone && <span className="customer-cell-phone">{formatPhone(b.phone)}</span>}
                                    </span>
                                </td>
                                <td className="table-data" data-label="Barber">{b.barberName || <span className="no-actions">&mdash;</span>}</td>
                                <td className="table-data" data-label="Date & Time">{format(new Date(b.startDateTime), "dd-MM-yyyy")} <br />
                                    {format(new Date(b.startDateTime), "HH:mm")}
                                </td>
                                {/* Was the bare number - no currency and no decimals, so a €25.50 booking
                                    read "25.5" in a column headed "Amount". Dash when nothing is on file
                                    yet, matching the barber's own table and the Payment cell below. */}
                                <td className="table-data col-amount" data-label="Amount">{formatEuro(b.amount) ?? <span className="no-actions">&mdash;</span>}</td>
                                <td className="table-data" data-label="Status">
                                    <span className={`booking-status-badge ${isCancelled(b) ? "status-cancelled" : "status-confirmed"}`}>
                                        {isCancelled(b) ? "Cancelled" : "Confirmed"}
                                    </span>
                                </td>
                                {/* Labels match the filter's Paid/Unpaid pills - the raw enum used to read
                                    "COMPLETED" for a row the Paid filter had just returned. Payment is a
                                    LEFT JOIN, so a booking with no payment row at all reads null - that's a
                                    dash, not "Unpaid", which would imply there's money still to collect. */}
                                <td className="table-data" data-label="Payment">
                                    {b.paymentStatus == null ? (
                                        <span className="no-actions">&mdash;</span>
                                    ) : (
                                        <span className={`payment-status-badge ${b.paymentStatus === "COMPLETED" ? "payment-paid" : "payment-unpaid"}`}>
                                            {b.paymentStatus === "COMPLETED" ? "Paid" : "Unpaid"}
                                        </span>
                                    )}
                                </td>
                                <td className="table-data" data-label="Actions">
                                    <div className="action-menu-container">
                                        {/* Edit / Cancel / Mark-as-paid are all rejected by the backend once a
                                            booking is cancelled, so they're dropped rather than left to fail.
                                            Mark reviewed stays: a cancelled booking is exactly what gets
                                            flagged when its cancellation email couldn't be delivered. */}
                                        {hasActions(b) ? (
                                        <>
                                        <button className="three-dots-btn"
                                            onClick={(e) => toggleActionMenu(e, b.id)}
                                        >⋮
                                        </button>
                                            {openMenuId === b.id && (
                                                <div className="action-dropdown-menu" style={menuStyle}>
                                                    {!isCancelled(b) && (
                                                    <>
                                                    <button className="action-dropdown-item edit-item" disabled={isPast(b)} title={isPast(b) ? "This booking has already passed" : undefined} onClick={() => {
                                                    /* Hand over the view we're on (filters live in the query
                                                       string) so saving comes back to it. Without this the
                                                       edit page returns to a bare "/admin", which drops the
                                                       needs-review worklist and hides the row the admin
                                                       still has to mark reviewed. */
                                                    navigate(`/datetime/${b.id}`, { state: { from: `${location.pathname}${location.search}` } }); setOpenMenuId(null);
                                                    }}><SquarePen size={14}/> Edit</button>
                                                <button className="action-dropdown-item cancel-item" disabled={isPast(b)} title={isPast(b) ? "This booking has already passed" : undefined} onClick={() => {
                                                    setCancelTarget(b); setRefundAnyway(false); setOpenMenuId(null);
                                                    }}><Trash2 size={14} /> Cancel</button>
                                                    </>
                                                    )}
                                                {b.needsReview && (
                                                    <button className="action-dropdown-item review-item" onClick={() => {
                                                        setReviewTarget(b); setOpenMenuId(null);
                                                    }}><CheckCircle2 size={14} /> Mark reviewed</button>
                                                )}
                                                {!isCancelled(b) && b.paymentMethod === "CASH" && b.paymentStatus === "PENDING" && (
                                                    <button className="action-dropdown-item mark-paid-item" onClick={() => {
                                                        setOpenMenuId(null);
                                                        /* Customer cash bookings already have an amount on file -> one-click.
                                                         * Admin bookings (no amount) open a dialog to optionally capture it. */
                                                        if (b.amount != null) {
                                                            onMarkPaid(b.id, null);
                                                        } else {
                                                            setMarkPaidTarget(b); setMarkPaidAmount("");
                                                        }
                                                    }}><Banknote size={14} /> Mark as paid</button>
                                                )}
                                                {!isCancelled(b) && b.paymentMethod === "CASH" && b.paymentStatus === "COMPLETED" && (
                                                    <button className="action-dropdown-item mark-paid-item" onClick={() => {
                                                        setOpenMenuId(null);
                                                        setEditAmountTarget(b);
                                                        setEditAmountValue(b.amount != null ? String(b.amount) : "");
                                                    }}><Coins size={14} /> Edit amount</button>
                                                )}
                                                </div>
                                            )}
                                        </>
                                        ) : (
                                            <span className="no-actions">&mdash;</span>
                                        )}
                                    </div>
                                </td>
                            </tr>
                            {/* The reason is read out in full rather than hidden behind a hover on a 14px
                                icon - you can't scan or compare tooltips. Shown on every flagged booking,
                                not just inside the needs-review filter: a flag the admin stumbles on while
                                browsing is exactly when they least expect it and most need telling why.
                                Its own full-width row keeps the columns above aligned. */}
                            {b.needsReview && (
                                <tr className="needs-review-reason-row">
                                    <td className="needs-review-reason-cell" colSpan={8}>
                                        {b.reviewReason || "Flagged for manual review - no reason was recorded."}
                                        {/* Only once they've actually done something to this booking. The
                                            prompt is the whole point of the marker: you dealt with it, so
                                            here's the button to take it off the list, right where you're
                                            already looking instead of back in the row menu. */}
                                        {touched && (
                                            <span className="review-resolve-prompt">
                                                You just {touched === "cancelled" ? "cancelled" : "edited"} this &mdash; done with it?
                                                <button
                                                    type="button"
                                                    className="review-resolve-btn"
                                                    onClick={() => setReviewTarget(b)}
                                                >
                                                    <CheckCircle2 size={13} /> Mark resolved
                                                </button>
                                            </span>
                                        )}
                                    </td>
                                </tr>
                            )}
                            </Fragment>
                            );
                        })}
                    </tbody>
                </table>
                {loading && bookings.length > 0 && (
                    <div className="table-busy-overlay">
                        <LoadingSpinner color="#e0e0e0" inline />
                    </div>
                )}
            </div>
            )}
            <Pagination page={page} totalPages={totalPages} onChange={onPageChange} />
            {filterModalOpen && (
                <>
                    <FilterModal setFilterModalOpen={setFilterModalOpen} filters={filters} resetFilters={resetFilters} applyFilters={applyFilters} />
                </>
            )}
            {cancelTarget && (
                <div className="cancel-confirm-overlay" onClick={() => setCancelTarget(null)}>
                    <div className="cancel-confirm-modal" onClick={(e) => e.stopPropagation()}>
                        <h3 className="cancel-confirm-title">Cancel booking #{cancelTarget.id}?</h3>
                        {isWithinRefundCutoff(cancelTarget) ? (
                            <>
                                <p className="cancel-confirm-text">
                                    This appointment is within {refundCutoffHours} hours. Per the cancellation
                                    policy, <strong>no refund</strong> will be issued.
                                </p>
                                <label className="cancel-refund-override">
                                    <input
                                        type="checkbox"
                                        checked={refundAnyway}
                                        onChange={(e) => setRefundAnyway(e.target.checked)}
                                    />
                                    Issue a full refund anyway
                                </label>
                            </>
                        ) : (
                            <p className="cancel-confirm-text">
                                This appointment is more than {refundCutoffHours} hours away. If the customer
                                paid by card, a <strong>full refund</strong> will be issued.
                            </p>
                        )}
                        <div className="cancel-confirm-actions">
                            <button className="cancel-confirm-keep" onClick={() => setCancelTarget(null)}>Keep booking</button>
                            <button className="cancel-confirm-go" onClick={confirmCancel}>Confirm cancel</button>
                        </div>
                    </div>
                </div>
            )}
            {reviewTarget && (
                <div className="cancel-confirm-overlay" onClick={() => setReviewTarget(null)}>
                    <div className="cancel-confirm-modal" onClick={(e) => e.stopPropagation()}>
                        <h3 className="cancel-confirm-title">Mark booking #{reviewTarget.id} as reviewed?</h3>
                        {reviewTarget.reviewReason && (
                            <p className="cancel-confirm-text review-reason-text">{reviewTarget.reviewReason}</p>
                        )}
                        <p className="cancel-confirm-text">
                            This only clears the review flag. Make sure you've already refunded / reconciled
                            this booking in Stripe first &mdash; marking it reviewed does <strong>not</strong> move any money.
                        </p>
                        <div className="cancel-confirm-actions">
                            <button className="cancel-confirm-keep" onClick={() => setReviewTarget(null)}>Cancel</button>
                            <button className="review-confirm-go" onClick={confirmReview}>Mark reviewed</button>
                        </div>
                    </div>
                </div>
            )}
            {markPaidTarget && (
                <div className="cancel-confirm-overlay" onClick={() => setMarkPaidTarget(null)}>
                    <div className="cancel-confirm-modal" onClick={(e) => e.stopPropagation()}>
                        <h3 className="cancel-confirm-title">Mark booking #{markPaidTarget.id} as paid?</h3>
                        <p className="cancel-confirm-text">
                            This booking has no amount on file. Enter the cash you collected to keep revenue
                            accurate &mdash; or leave it blank if you don't have it, and it'll still be marked
                            as collected.
                        </p>
                        <label className="mark-paid-amount-label">
                            Amount collected (&euro;)
                            <input
                                type="number"
                                min="0"
                                max="400"
                                step="0.01"
                                inputMode="decimal"
                                className={`mark-paid-amount-input${markPaidAmountError ? " form-input--invalid" : ""}`}
                                placeholder="Optional"
                                value={markPaidAmount}
                                onChange={(e) => setMarkPaidAmount(e.target.value)}
                            />
                            {markPaidAmountError && <span className="form-error">{markPaidAmountError}</span>}
                        </label>
                        <div className="cancel-confirm-actions">
                            <button className="cancel-confirm-keep" onClick={() => setMarkPaidTarget(null)}>Cancel</button>
                            <button className="review-confirm-go" onClick={confirmMarkPaid} disabled={!!markPaidAmountError}>Mark as paid</button>
                        </div>
                    </div>
                </div>
            )}
            {editAmountTarget && (
                <div className="cancel-confirm-overlay" onClick={() => setEditAmountTarget(null)}>
                    <div className="cancel-confirm-modal" onClick={(e) => e.stopPropagation()}>
                        <h3 className="cancel-confirm-title">Edit amount for booking #{editAmountTarget.id}</h3>
                        <p className="cancel-confirm-text">
                            Set the amount actually collected for this booking &mdash; e.g. a discount given on
                            the day. This updates the revenue chart and stat cards.
                        </p>
                        <label className="mark-paid-amount-label">
                            Amount collected (&euro;)
                            <input
                                type="number"
                                min="0"
                                max="400"
                                step="0.01"
                                inputMode="decimal"
                                className={`mark-paid-amount-input${editAmountError ? " form-input--invalid" : ""}`}
                                value={editAmountValue}
                                onChange={(e) => setEditAmountValue(e.target.value)}
                            />
                            {editAmountError && <span className="form-error">{editAmountError}</span>}
                        </label>
                        <div className="cancel-confirm-actions">
                            <button className="cancel-confirm-keep" onClick={() => setEditAmountTarget(null)}>Cancel</button>
                            <button className="review-confirm-go" onClick={confirmEditAmount} disabled={!!editAmountError}>Save amount</button>
                        </div>
                    </div>
                </div>
            )}
        </div>
    )
}
export default BookingsTable;