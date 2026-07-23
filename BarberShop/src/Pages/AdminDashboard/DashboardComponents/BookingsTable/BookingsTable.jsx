import { useState, useEffect, useContext } from "react";
import { useNavigate } from "react-router-dom";
import { SquarePen, Trash2, Plus, Filter, Search, AlertTriangle, CheckCircle2, Banknote } from "lucide-react";
import { format } from "date-fns";
import FilterModal from "../Filter/Filter.jsx";
import "./BookingsTable.css";
import { adminAxios } from "../../../../Hooks/AxiosInterceptor";
import { ToastContext } from "../../../../Context/ToastContext.jsx";
import Pagination from "../../../../Components/Pagination/Pagination.jsx";
import LoadingSpinner from "../../../../Components/LoadingSpinner/LoadingSpinner.jsx";
import ErrorState from "../../../../Components/ErrorState/ErrorState.jsx";
import { getErrorMessage } from "../../../../utils/errorMessage.js";

/* Cancelling within this many hours of the appointment forfeits the customer's refund (mirrors the
 * backend RefundCutoff in BookingCanceller). Compared against Malta wall-clock, since startDateTime is
 * stored in Malta time - see getMaltaNow. */
const REFUND_CUTOFF_HOURS = 24;
const getMaltaNow = () =>
    new Date(new Date().toLocaleString("en-US", { timeZone: "Europe/Malta" }));

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
    const { showToast } = useContext(ToastContext);
    const navigate = useNavigate();

    /* startDateTime is Malta wall-clock (parsed as local) and getMaltaNow() is Malta's clock as local, so
     * this difference is a true Malta-vs-Malta comparison regardless of the admin's own timezone. */
    const isWithinRefundCutoff = (booking) => {
        const diffHours = (new Date(booking.startDateTime) - getMaltaNow()) / (1000 * 60 * 60);
        return diffHours < REFUND_CUTOFF_HOURS;
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
                            <th className="table-header">Name</th>
                            <th className="table-header">Date & Time</th>
                            <th className="table-header">Amount</th>
                            <th className="table-header">Status</th>
                            <th className="table-header">Payment</th>
                            <th className="table-header">Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        {bookings.map((b) => (
                            <tr key={b.id} style={{ transition: "background 0.2s" }}>
                                <td className="table-data" data-label="Booking ID">
                                    <span className="booking-id-cell">
                                        {b.id}
                                        {b.needsReview && (
                                            <AlertTriangle
                                                size={14}
                                                className="needs-review-flag"
                                                aria-label="Needs review"
                                                title={b.reviewReason || "Needs manual review"}
                                            />
                                        )}
                                    </span>
                                </td>
                                <td className="table-data" data-label="Name">{`${b.firstName}`}</td>
                                <td className="table-data" data-label="Date & Time">{format(new Date(b.startDateTime), "dd-MM-yyyy")} <br />
                                    {format(new Date(b.startDateTime), "HH:mm")}
                                </td>
                                <td className="table-data" data-label="Amount">{b.amount}</td>
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
                                                    navigate(`/datetime/${b.id}`); setOpenMenuId(null);
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
                                                </div>
                                            )}
                                        </>
                                        ) : (
                                            <span className="no-actions">&mdash;</span>
                                        )}
                                    </div>
                                </td>
                            </tr>
                        ))}
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
                                    This appointment is within {REFUND_CUTOFF_HOURS} hours. Per the cancellation
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
                                This appointment is more than {REFUND_CUTOFF_HOURS} hours away. If the customer
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
        </div>
    )
}
export default BookingsTable;