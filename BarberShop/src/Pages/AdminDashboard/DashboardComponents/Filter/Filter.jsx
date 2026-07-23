import "./Filter.css";
import CalendarModal from "./Calendar/CalendarModal.jsx";
import { format, startOfDay, endOfWeek, startOfWeek, startOfMonth, endOfMonth } from "date-fns";
import { useRef, useEffect, useState, useContext, useMemo } from "react";
import { X, Calendar } from "lucide-react";
import { ToastContext } from "../../../../Context/ToastContext.jsx";
const FilterModal = ({ setFilterModalOpen,filters, resetFilters, applyFilters }) => {
    const { showToast } = useContext(ToastContext);
    const [selectedFromDate, setSelectedFromDate] = useState(filters.fromDate);
    const [selectedToDate, setSelectedToDate] = useState(filters.toDate);
    /* Two separate axes. null on either means its default: CONFIRMED bookings / any payment status. */
    const [bookingStatusFilter, setBookingStatusFilter] = useState(filters.bookingStatus);
    const [paymentStatusFilter, setPaymentStatusFilter] = useState(filters.paymentStatus);
    useEffect(() => {
        setSelectedFromDate(filters.fromDate);
        setSelectedToDate(filters.toDate);
        setBookingStatusFilter(filters.bookingStatus);
        setPaymentStatusFilter(filters.paymentStatus);
    }, [filters]);
    // single state
    // derive the params from it
    const [fromCalendar, setFromCalendar] = useState(false);

    const [toCalendar, setToCalendar] = useState(false);
    const toRef = useRef(null);
    const fromRef = useRef(null);
    const today = startOfDay(new Date());
    useEffect(() => {
        const handleClickOutside = (e) => {
            if (fromRef.current && !fromRef.current.contains(e.target)) {
                setFromCalendar(false);
            }
            if (toRef.current && !toRef.current.contains(e.target)) {
                setToCalendar(false);
            }
        }
        document.addEventListener("mousedown", handleClickOutside);
        return () => document.removeEventListener("mousedown", handleClickOutside);
    }, [])

    const reset = () => {
        resetFilters();
        setFromCalendar(false);
        setToCalendar(false);
        setFilterModalOpen(false);
    }

    const ApplyFilter = async () => {
        if ((selectedFromDate && !selectedToDate) || (selectedToDate && !selectedFromDate)) { showToast("Filter Failed", "Both Dates need to be provided or empty"); return; }
        /* Applying the filter always leaves the needs-review worklist - it ignores both axes, so staying in
         * it would silently discard whatever the admin just picked here. */
        applyFilters({
            bookingStatus: bookingStatusFilter,
            paymentStatus: paymentStatusFilter,
            needsReview: false,
            fromDate: selectedFromDate,
            toDate: selectedToDate
        });
        setFilterModalOpen(false);
        }

    const activeShortcut = useMemo(() => {
        if (!selectedFromDate && !selectedToDate) return "ALL";
        if (
            selectedFromDate?.toDateString() === today.toDateString() &&
            selectedToDate?.toDateString() === today.toDateString()
        ) return "TODAY";
        if (
            selectedFromDate?.toDateString() === startOfWeek(today).toDateString() &&
            selectedToDate?.toDateString() === endOfWeek(today).toDateString()
        ) return "WEEK";
        if (
            selectedFromDate?.toDateString() === startOfMonth(today).toDateString() &&
            selectedToDate?.toDateString() === endOfMonth(today).toDateString()
        ) return "MONTH";
        return null;
    }, [selectedFromDate, selectedToDate]);
    return (
        <div className="modal-overlay" onClick={() => setFilterModalOpen(false)}>
            <div className="filter-modal" onClick={(e) => e.stopPropagation()}>
                <div className="filter-modal-header">
                    <h3 className="filter-modal-title">Filter Bookings</h3>
                    <button onClick={() => setFilterModalOpen(false)} className="filter-modal-close" aria-label="Close">
                        <X size={18} />
                    </button>
                </div>

                <div className="filter-modal-fields">

                    {/* Booking status and payment are separate axes so any combination is reachable -
                        e.g. cancelled-but-still-paid, the rows where a refund didn't go through. */}
                    <div>
                        <label className="filter-field-label">Booking Status</label>
                        <div className="pill-group">
                            <button onClick={() => setBookingStatusFilter(null)} className={`pill ${bookingStatusFilter === null ? "pill-active" : "pill-inactive"}`}>Confirmed</button>
                            <button onClick={() => setBookingStatusFilter("CANCELLED")} className={`pill ${bookingStatusFilter === "CANCELLED" ? "pill-active" : "pill-inactive"}`}>Cancelled</button>
                            <button onClick={() => setBookingStatusFilter("ALL")} className={`pill ${bookingStatusFilter === "ALL" ? "pill-active" : "pill-inactive"}`}>All</button>
                        </div>
                    </div>

                    <div>
                        <label className="filter-field-label">Payment</label>
                        <div className="pill-group">
                            <button onClick={() => setPaymentStatusFilter(null)} className={`pill ${paymentStatusFilter === null ? "pill-active" : "pill-inactive"}`}>Any</button>
                            <button onClick={() => setPaymentStatusFilter("PAID")} className={`pill ${paymentStatusFilter === "PAID" ? "pill-active" : "pill-inactive"}`}>Paid</button>
                            <button onClick={() => setPaymentStatusFilter("UNPAID")} className={`pill ${paymentStatusFilter === "UNPAID" ? "pill-active" : "pill-inactive"}`}>Unpaid</button>
                        </div>
                    </div>
                    <div className="modal-calendar-shortcuts">
                        <button className={`modal-calendar-shortcut ${activeShortcut === "TODAY" ? "shortcut-active" : ""}`} onClick={() => {
                            setSelectedFromDate(today); setSelectedToDate(today);
                    }}>Today</button>

                        <button className={`modal-calendar-shortcut ${activeShortcut === "WEEK" ? "shortcut-active" : ""}`} onClick={() => {
                        setSelectedFromDate(startOfWeek(today));
                        setSelectedToDate(endOfWeek(today));
                    }}>This week</button>

                        <button className={`modal-calendar-shortcut ${activeShortcut === "MONTH" ? "shortcut-active" : ""}`} onClick={() => {
                        setSelectedFromDate(startOfMonth(today));
                        setSelectedToDate(endOfMonth(today));
                        }}>This month</button>
                        <button className={`modal-calendar-shortcut ${activeShortcut === "ALL" ? "shortcut-active" : ""}`} onClick={() => {
                            setSelectedFromDate(null);
                            setSelectedToDate(null);
                        }}>All</button>
                    </div>

                    <div className="filter-date-grid">
                        <div>
                            <label className="filter-field-label">From</label>
                            <div style={{ position: "relative" }} ref={fromRef}>
                                <div onClick={() => setFromCalendar(!fromCalendar)} className="filter-modal-input">
                                <span>{selectedFromDate ? format(selectedFromDate, "dd-MM-yyyy") : "dd-mm-yyyy"}</span>
                                    <Calendar size={17} />
                                </div>
                            
                            {fromCalendar && (
                                    <CalendarModal mode="From" selectedToDate={selectedToDate} selectedFromDate={selectedFromDate} setSelectedFromDate={setSelectedFromDate} setSelectedToDate={setSelectedToDate} />
                                    
                                )}
                            </div>
                        </div>
                        <div>
                            <label className="filter-field-label">To</label>
                            <div ref={toRef} style={{position:"relative"}}>
                                <div onClick={() => setToCalendar(!toCalendar)} className="filter-modal-input">
                                    <span>{selectedToDate ? format(selectedToDate, "dd-MM-yyyy") : "dd-mm-yyyy"}</span>
                                    <Calendar size={17}/>
                                </div>
                                {toCalendar && (
                                    <CalendarModal
                                        mode="To"
                                        selectedToDate={selectedToDate}
                                        selectedFromDate={selectedFromDate}
                                        setSelectedFromDate={setSelectedFromDate}
                                        setSelectedToDate={setSelectedToDate}
                                        />
                                    
                                )}
                            </div>
                        </div>

                    </div>

                    <div className="filter-modal-footer">
                        <button className="filter-btn-modal filter-btn-reset" onClick={reset}>Reset</button>
                        <button className="filter-btn-modal filter-btn-apply" onClick={ApplyFilter}>Apply Filters</button>
                    </div>
                </div>
            </div>
            </div>
            )
}
            export default FilterModal;