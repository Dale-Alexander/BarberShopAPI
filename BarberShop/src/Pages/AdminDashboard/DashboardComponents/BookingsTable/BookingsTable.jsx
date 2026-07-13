import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { SquarePen, Trash2, Plus, Filter, Search } from "lucide-react";
import { format } from "date-fns";
import FilterModal from "../Filter/Filter.jsx";
import "./BookingsTable.css";
import { adminAxios } from "../../../../Hooks/AxiosInterceptor";

/* Cancelling within this many hours of the appointment forfeits the customer's refund (mirrors the
 * backend RefundCutoff in BookingCanceller). Compared against Malta wall-clock, since startDateTime is
 * stored in Malta time - see getMaltaNow. */
const REFUND_CUTOFF_HOURS = 24;
const getMaltaNow = () =>
    new Date(new Date().toLocaleString("en-US", { timeZone: "Europe/Malta" }));

const BookingsTable = ({ bookings,setBookings, resetFilters, applyFilters, filters}) => {
    const [openMenuId, setOpenMenuId] = useState(null);
    const [searchInput, setSearchInput] = useState("");
    const [filterModalOpen, setFilterModalOpen] = useState(false);
    const [cancelTarget, setCancelTarget] = useState(null);
    const [refundAnyway, setRefundAnyway] = useState(false);
    const navigate = useNavigate();

    /* startDateTime is Malta wall-clock (parsed as local) and getMaltaNow() is Malta's clock as local, so
     * this difference is a true Malta-vs-Malta comparison regardless of the admin's own timezone. */
    const isWithinRefundCutoff = (booking) => {
        const diffHours = (new Date(booking.startDateTime) - getMaltaNow()) / (1000 * 60 * 60);
        return diffHours < REFUND_CUTOFF_HOURS;
    };
    useEffect(() => {
        const handleClickOutside = () => {
            setOpenMenuId(null);
        }
        if (openMenuId != null) {
            document.addEventListener("click", handleClickOutside);
        }
        return () => document.removeEventListener("click", handleClickOutside);
    }, [openMenuId])

    const onCancel = async (bookingId, forceRefund = false) => {
        try {
            await adminAxios.patch(`/api/bookings/cancel/${bookingId}${forceRefund ? "?refundAnyway=true" : ""}`);
            setBookings(prev => prev.filter(b => b.id !== bookingId));
        }
        catch (err) {
            console.error(err.response?.data?.message || err.response?.data);
        }
    }

    const confirmCancel = () => {
        if (!cancelTarget) return;
        /* refundAnyway only matters inside the cutoff; outside it a full refund happens automatically. */
        const force = isWithinRefundCutoff(cancelTarget) && refundAnyway;
        onCancel(cancelTarget.id, force);
        setCancelTarget(null);
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
            <div style={{ overflowX: "auto" }}>
                <table className="bookings-table">
                    <thead>
                        <tr>
                            <th className="table-header">Booking ID</th>
                            <th className="table-header">Name</th>
                            <th className="table-header">Date & Time</th>
                            <th className="table-header">Amount</th>
                            <th className="table-header">Payment Status</th>
                            <th className="table-header">Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        {bookings.map((b) => (
                            <tr key={b.id} style={{ transition: "background 0.2s" }}>
                                <td className="table-data" data-label="Booking ID">{b.id}</td>
                                <td className="table-data" data-label="Name">{`${b.firstName}`}</td>
                                <td className="table-data" data-label="Date & Time">{format(new Date(b.startDateTime), "dd-MM-yyyy")} <br />
                                    {format(new Date(b.startDateTime), "HH:mm")}
                                </td>
                                <td className="table-data" data-label="Amount">{b.amount}</td>
                                <td className="table-data" data-label="Payment Status">{b.paymentStatus}</td>
                                <td className="table-data" data-label="Actions">
                                    <div className="action-menu-container">
                                        <button className="three-dots-btn"
                                            onClick={(e) => {
                                                setOpenMenuId(openMenuId === b.id ? null : b.id);
                                                e.stopPropagation();
                                            }}
                                        >⋮
                                        </button>
                                            {openMenuId === b.id && (
                                                <div className="action-dropdown-menu">
                                                    <button className="action-dropdown-item edit-item" onClick={() => {
                                                    navigate(`/datetime/${b.id}`); setOpenMenuId(null);
                                                    }}><SquarePen size={14}/> Edit</button>
                                                <button className="action-dropdown-item cancel-item" onClick={() => {
                                                    setCancelTarget(b); setRefundAnyway(false); setOpenMenuId(null);
                                                    }}><Trash2 size={14} /> Cancel</button>
                                                </div>
                                            )}
                                    </div>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>
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
        </div>
    )
}
export default BookingsTable;