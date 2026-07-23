import { useParams, useNavigate} from "react-router-dom";
import { format } from "date-fns";
import { useContext, useState, useEffect, useCallback } from "react";
import { usePersistentFilters } from "../../Hooks/UsePersistentFilters.js";
import { BarberBookings as BarberBookingsContext } from "../../Context/BarberContext";
import "./BarberBookings.css";
// Reuse the admin bookings-table styles for the row action menu + confirm modals so the barber's
// own-bookings actions look identical to the admin's.
import "../AdminDashboard/DashboardComponents/BookingsTable/BookingsTable.css";
import { ArrowLeft, UserX, Filter, SquarePen, Trash2, Banknote, Plus } from "lucide-react";
import LoadingSpinner from "../../Components/LoadingSpinner/LoadingSpinner";
import ErrorState from "../../Components/ErrorState/ErrorState";
import useFetchBarberBookings from "../../Hooks/UseFetchBarberBookings.js";
import { fetchBarberSummary } from "../../utils/FetchBarberSummary.js";
import Pagination from "../../Components/Pagination/Pagination.jsx";
import { resolveBarberImage, handleBarberImageError } from "../../utils/barberImage.js";
import FilterModal from "../AdminDashboard/DashboardComponents/Filter/Filter.jsx";
import { adminAxios } from "../../Hooks/AxiosInterceptor";
import { ToastContext } from "../../Context/ToastContext.jsx";
import { AuthContext } from "../../Context/AuthContext.jsx";
import { getErrorMessage } from "../../utils/errorMessage.js";

/* Within this many hours of the appointment a cancellation forfeits the customer's refund (mirrors the
 * backend RefundCutoff in BookingCanceller). Compared against Malta wall-clock - same as the admin table. */
const REFUND_CUTOFF_HOURS = 24;
const getMaltaNow = () =>
    new Date(new Date().toLocaleString("en-US", { timeZone: "Europe/Malta" }));

const BarberBookings = () => {
    const { id } = useParams();
    const { load, loading, error } = useFetchBarberBookings();
    const { filters, applyFilters, resetFilters } = usePersistentFilters();
    const { viewBookingsBarber } = useContext(BarberBookingsContext);
    const { showToast } = useContext(ToastContext);
    const { user } = useContext(AuthContext);
    const navigate = useNavigate();
    // "Back to Team" only makes sense for an admin browsing barbers; a barber viewing their own
    // dashboard has no team list to go back to (and can't reach /admin/team anyway), so hide it.
    const isAdminViewer = user?.role === "ADMIN";

    const [barberBookings, setBarberBookings] = useState(null);
    // Tiles (total bookings / distinct clients / distinct services) now come from the server-side
    // barber-summary endpoint, computed over the full filtered set rather than the current page.
    const [summary, setSummary] = useState(null);
    const [page, setPage] = useState(1);
    const [totalPages, setTotalPages] = useState(1);
    const [filterModalOpen, setFilterModalOpen] = useState(false);

    // Row action menu (viewport-fixed so it escapes the table's overflow), plus the confirm dialogs -
    // same interaction model as the admin BookingsTable.
    const [openMenuId, setOpenMenuId] = useState(null);
    const [menuStyle, setMenuStyle] = useState(null);
    const [cancelTarget, setCancelTarget] = useState(null);
    const [refundAnyway, setRefundAnyway] = useState(false);
    const [markPaidTarget, setMarkPaidTarget] = useState(null);
    const [markPaidAmount, setMarkPaidAmount] = useState("");

    // A filter or barber change restarts paging at page 1.
    useEffect(() => { setPage(1); }, [filters.fromDate, filters.toDate, filters.status, id]);

    // Hoisted so the error state's Retry can re-run it. `load` is left out of the deps on purpose
    // (re-created each render but closes over stable values); the real triggers are filters/id/page.
    const loadBookings = useCallback(async () => {
        const data = await load({
            fromDate: filters.fromDate,
            toDate: filters.toDate,
            status: filters.status,
            barberId: Number(id),
            page,
        })
        if (data) {
            setBarberBookings(data);
            setTotalPages(data.totalPages ?? 1);
        }
    }, [filters.fromDate, filters.toDate, filters.status, id, page]);

    useEffect(() => { loadBookings(); }, [loadBookings]);

    const loadSummary = useCallback(async () => {
        try {
            const data = await fetchBarberSummary({
                barberId: Number(id),
                fromDate: filters.fromDate,
                toDate: filters.toDate,
                status: filters.status,
            });
            setSummary(data);
        }
        catch (err) {
            // Non-critical tiles - leave them as-is rather than blocking the page on a toast.
            console.error(err.response?.data?.message || err);
        }
    }, [filters.fromDate, filters.toDate, filters.status, id]);

    useEffect(() => { loadSummary(); }, [loadSummary]);

    useEffect(() => {
        console.log(viewBookingsBarber);
    }, [viewBookingsBarber])

    // Close the action menu on any outside click / scroll / resize (the menu is viewport-fixed).
    useEffect(() => {
        if (openMenuId == null) return;
        const close = () => setOpenMenuId(null);
        document.addEventListener("click", close);
        window.addEventListener("scroll", close, true);
        window.addEventListener("resize", close);
        return () => {
            document.removeEventListener("click", close);
            window.removeEventListener("scroll", close, true);
            window.removeEventListener("resize", close);
        };
    }, [openMenuId]);

    /* A booking that has already passed can't be rescheduled or cancelled - the backend rejects both,
     * so we grey the actions out. Malta-vs-Malta comparison. */
    const isPast = (booking) => new Date(booking.startDateTime) < getMaltaNow();
    const isWithinRefundCutoff = (booking) => {
        const diffHours = (new Date(booking.startDateTime) - getMaltaNow()) / (1000 * 60 * 60);
        return diffHours < REFUND_CUTOFF_HOURS;
    };

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

    // Update a single booking row in place, or drop it, without a full refetch.
    const updateRow = (bookingId, updater) => {
        setBarberBookings(prev => prev ? { ...prev, bookings: prev.bookings.map(b => b.id === bookingId ? updater(b) : b) } : prev);
    };
    const removeRow = (bookingId) => {
        setBarberBookings(prev => prev ? { ...prev, bookings: prev.bookings.filter(b => b.id !== bookingId) } : prev);
    };

    const onCancel = async (bookingId, forceRefund = false) => {
        try {
            const res = await adminAxios.patch(`/api/bookings/cancel/${bookingId}${forceRefund ? "?refundAnyway=true" : ""}`);
            // Cancelling drops it out of the default COMPLETED view; refresh the tiles so counts follow.
            removeRow(bookingId);
            loadSummary();
            showToast("Booking cancelled", res.data?.message || "The booking was cancelled.", "success");
        }
        catch (err) {
            showToast("Cancellation failed", getErrorMessage(err));
        }
    };

    const confirmCancel = () => {
        if (!cancelTarget) return;
        const force = isWithinRefundCutoff(cancelTarget) && refundAnyway;
        onCancel(cancelTarget.id, force);
        setCancelTarget(null);
    };

    /* Cash bookings sit as COMPLETED booking + PENDING payment until collected in person. This flips the
     * payment to COMPLETED. `amount` is only sent for phone bookings (no amount on file); customer cash
     * bookings keep their stored amount and pass null. Mirrors the admin BookingsTable. */
    const onMarkPaid = async (bookingId, amount) => {
        try {
            const res = await adminAxios.patch(`/api/bookings/mark-cash-paid/${bookingId}`, amount != null ? { amount } : {});
            updateRow(bookingId, b => ({ ...b, paymentStatus: "COMPLETED", amount: res.data?.amount ?? b.amount }));
            loadSummary();
            showToast("Marked as paid", res.data?.message || `Booking #${bookingId} was marked as collected.`, "success");
        }
        catch (err) {
            showToast("Couldn't mark as paid", getErrorMessage(err));
        }
    };

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
    };

    // Only blank the whole page on the very first load; page changes keep the table in place.
    if (loading && !barberBookings) {
        return <LoadingSpinner message={"Loading Barber Data"} color="#e0e0e0"/>
    }

    // A real failed load (network / 500) shouldn't masquerade as a missing barber - offer a Retry.
    if (!barberBookings && error && error.response?.status !== 404) return (
        <ErrorState
            title="Couldn't load barber"
            message="We couldn't load this barber's bookings. Please try again."
            onRetry={loadBookings}
        />
    );

    if (!barberBookings) return (
        <div className="barber-error-state">
            <UserX size={48} />
            <h2>Barber Not Found</h2>
            <p>The barber you're looking for doesn't exist.</p>
            <div className = "barber-bookings-top-right">
            {isAdminViewer && (
                <button className="back-to-team" onClick={() => navigate("/admin/team")}>
                    <ArrowLeft size={15} /> BACK TO TEAM
                </button>
            )}
            <section aria-label="Filter">
                <button onClick={() => setFilterModalOpen(true)} className="bookings-admin-table-btn bookings-admin-table-btn-filter">
                    <span className="bookings-admin-table-btn-icon"><Filter size={16} /></span>
                    Filter
                </button>
                </section>
            </div>
        </div>
    );

    return (
        <>
            <div className="page-header">
                <div className="page-header-text">
                    <h1 className="page-title">Bookings</h1>
                    <p className="page-subtitle">Viewing bookings for {barberBookings?.barberName} {barberBookings?.barberSurname}</p>
                </div>
                <div className="barber-bookings-top-right">
                    {/* Hidden for a deactivated barber: no booking can be made for them (both create
                        endpoints filter on isActive), so offering it here only dead-ends. Existing
                        bookings stay fully actionable - cancelling and reconciling an ex-barber's
                        appointments is exactly what this page is for. */}
                    {barberBookings?.barberIsActive !== false && (
                    <section aria-label="Create Booking" onClick={() => navigate("/datetime?adminBooking=true")}>
                        <button className="bookings-admin-table-btn bookings-admin-table-btn-create">
                            <span className="bookings-admin-table-btn-icon"><Plus size={16} /></span>
                            Create Booking
                        </button>
                    </section>
                    )}
                    <section aria-label="Filter">
                        <button onClick={() => setFilterModalOpen(true)} className="bookings-admin-table-btn bookings-admin-table-btn-filter-2">
                            <span className="bookings-admin-table-btn-icon"><Filter size={16} /></span>
                            Filter
                        </button>
                    </section>
                {isAdminViewer && (
                    <button className="back-to-team" onClick={() => navigate("/admin/team")}>
                        <ArrowLeft size={15}/> BACK TO TEAM
                    </button>
                )}
                </div>
            </div>

            {barberBookings?.barberIsActive === false && (
                <div className="barber-inactive-notice">
                    <UserX size={16} />
                    <span>
                        This barber is deactivated and can't be booked or sign in. Their past bookings are
                        shown below and can still be cancelled or marked as paid.
                    </span>
                </div>
            )}

            <div className="barber-bookings-content-area">
                <div className="barber-detail-card">
                    <div className="barber-detail-left">
                        <div className="barber-detail-avatar">
                            <img src={resolveBarberImage(barberBookings?.barberImageUrl)} onError={handleBarberImageError} alt={barberBookings?.barberName} />
                            {/* The status dot is hardcoded green as an "active" indicator, so it has to
                                go grey for a deactivated barber rather than claiming they're available. */}
                            <span className={`barber-detail-status${barberBookings?.barberIsActive === false ? " barber-detail-status--inactive" : ""}`} />
                        </div>
                        <div className="barber-detail-meta">
                            <h2 className="barber-detail-name">{barberBookings?.barberName} {barberBookings?.barberSurname}</h2>
                        </div>
                    </div>
                    <div className="barber-detail-stats">
                        <div className="barber-stat">
                            <span className="barber-stat-value">{summary?.totalBookings ?? 0}</span>
                            <span className="barber-stat-label">Bookings</span>
                        </div>
                        <div className="barber-stat">
                            <span className="barber-stat-value">{summary?.distinctClients ?? 0}</span>
                            <span className="barber-stat-label">Clients</span>
                        </div>
                        <div className="barber-stat">
                            <span className="barber-stat-value">{summary?.distinctServices ?? 0}</span>
                            <span className="barber-stat-label">Services</span>
                        </div>
                    </div>
                </div>

                {barberBookings?.bookings.length === 0 ? (
                    <p className="no-bookings">No bookings yet for this barber.</p>
                ) : (
                    <div className="bookings-table-wrap" style={{ position: "relative" }}>
                        <table className="barber-bookings-table">
                            <thead >
                                <tr className="barber-bookings-table-row">
                                        <th className="barber-bookings-table-header">Client</th>
                                        <th className="barber-bookings-table-header">Phone</th>
                                    <th className="barber-bookings-table-header">Date&Time</th>
                                    <th className="barber-bookings-table-header">Service</th>
                                    <th className="barber-bookings-table-header">Payment</th>
                                    <th className="barber-bookings-table-header">Actions</th>
                                </tr>
                            </thead>
                            <tbody>
                                {barberBookings?.bookings?.map((b) => (
                                    <tr key={b.id} className= "barber-bookings-table-row">
                                        <td className="barber-bookings-table-data" data-label="Client">{b.name} {b.surname}</td>
                                        <td className="barber-bookings-table-data" data-label="Phone">{ b.phone}</td>
                                        <td className="barber-bookings-table-data" data-label="Date & Time">
                                            <div className="datetime-cell">
                                                {format(new Date(b.startDateTime), "dd-MM-yyyy HH:mm")}
                                                {new Date(b.startDateTime) > new Date() ? (
                                                    <span className="booking-status upcoming">Upcoming</span>
                                                ): (
                                                        <span className="booking-status fulfilled">Fulfilled</span>
                                                ) }
                                            </div>
                                        </td>
                                        <td className="barber-bookings-table-data" data-label="Service">
                                            <div className="service-list">
                                                {b.services.map((s, index) => (
                                                    <div className="service-row" key={index}>
                                                        <span className="booking-service-badge">{s.serviceName}</span>
                                                        <span className="service-price">€{s.price}</span>
                                                    </div>
                                                ))}
                                            </div>
                                        </td>
                                        <td className="barber-bookings-table-data" data-label="Payment">{b.paymentStatus}</td>
                                        <td className="barber-bookings-table-data" data-label="Actions">
                                            <div className="action-menu-container">
                                                <button className="three-dots-btn" onClick={(e) => toggleActionMenu(e, b.id)}>⋮</button>
                                                {openMenuId === b.id && (
                                                    <div className="action-dropdown-menu" style={menuStyle}>
                                                        <button className="action-dropdown-item edit-item" disabled={isPast(b)} title={isPast(b) ? "This booking has already passed" : undefined} onClick={() => {
                                                            navigate(`/datetime/${b.id}`); setOpenMenuId(null);
                                                        }}><SquarePen size={14}/> Edit</button>
                                                        <button className="action-dropdown-item cancel-item" disabled={isPast(b)} title={isPast(b) ? "This booking has already passed" : undefined} onClick={() => {
                                                            setCancelTarget(b); setRefundAnyway(false); setOpenMenuId(null);
                                                        }}><Trash2 size={14} /> Cancel</button>
                                                        {b.paymentMethod === "CASH" && b.paymentStatus === "PENDING" && (
                                                            <button className="action-dropdown-item mark-paid-item" onClick={() => {
                                                                setOpenMenuId(null);
                                                                if (b.amount != null) {
                                                                    onMarkPaid(b.id, null);
                                                                } else {
                                                                    setMarkPaidTarget(b); setMarkPaidAmount("");
                                                                }
                                                            }}><Banknote size={14} /> Mark as paid</button>
                                                        )}
                                                    </div>
                                                )}
                                            </div>
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                        {/* First load is handled by the full-page spinner above; reaching here with loading true
                            is a page/filter refetch, so keep the current rows under a subtle busy overlay. */}
                        {loading && (
                            <div className="table-busy-overlay">
                                <LoadingSpinner color="#e0e0e0" inline />
                            </div>
                        )}
                    </div>
                )}
                <Pagination page={page} totalPages={totalPages} onChange={setPage} />
            </div>
            {filterModalOpen && (
                <>
                    <FilterModal setFilterModalOpen={setFilterModalOpen} filters={filters} applyFilters={applyFilters} resetFilters={resetFilters} />
                </>
            ) }
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
        </>
    )
}
export default BarberBookings;
