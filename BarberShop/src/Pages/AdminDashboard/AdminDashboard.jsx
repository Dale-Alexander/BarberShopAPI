import { useEffect, useState, useContext, useCallback } from "react";
import { adminAxios } from "../../Hooks/AxiosInterceptor.js";
import "./DashboardComponents/StatCard/StatCard";
import BookmarkIcon from '@mui/icons-material/Bookmark';
import EuroIcon from "@mui/icons-material/Euro";
import "./AdminDashboard.css";
import { usePersistentFilters } from "../../Hooks/UsePersistentFilters.js";
import StatCard from "./DashboardComponents/StatCard/StatCard";
import LineChart from "./DashboardComponents/LineChart/LineChart";
import LoadingSpinner from "../../Components/LoadingSpinner/LoadingSpinner.jsx";
import BookingsTable from "./DashboardComponents/BookingsTable/BookingsTable.jsx";
import { ChevronRight } from 'lucide-react';
import useFetchBookings from "../../Hooks/UseFetchBookings.js";
import { fetchAdminSummary } from "../../utils/FetchAdminSummary.js";
import { ToastContext } from "../../Context/ToastContext.jsx";


const AdminDashboard = () => {
    const [bookings, setBookings] = useState([]);
    const [page, setPage] = useState(1);
    const [totalPages, setTotalPages] = useState(1);
    const [selectedYear, setSelectedYear] = useState(new Date().getFullYear());
    const [selectedMonth, setSelectedMonth] = useState(null);
    /* Stat cards + performance chart now come from the server-side admin-summary endpoint (aggregated in
     * SQL) instead of being recomputed in the browser over the whole booking list - that full list is
     * exactly what pagination removed. */
    const [summary, setSummary] = useState(null);
    const [summaryLoading, setSummaryLoading] = useState(false);
    const { filters, applyFilters, resetFilters } = usePersistentFilters();
    const [isRevenue, setIsRevenue] = useState(true);
    const [yearOpen, setYearOpen] = useState(false);
    const { load, loading } = useFetchBookings();
    const { showToast } = useContext(ToastContext);
    const [needsReviewCount, setNeedsReviewCount] = useState(0);

    /* Standalone count so the "Needs Review" alert badge stays accurate no matter which date/status
     * filter the table currently has applied. Refreshed after a booking is marked reviewed. */
    const refreshNeedsReviewCount = useCallback(async () => {
        try {
            const res = await adminAxios.get("/api/bookings/needs-review-count");
            setNeedsReviewCount(res.data?.count ?? 0);
        }
        catch (err) {
            // Non-critical badge - don't toast, just leave it as-is.
            console.error(err.response?.data?.message || err);
        }
    }, []);

    useEffect(() => { refreshNeedsReviewCount(); }, [refreshNeedsReviewCount]);

    // Stat cards + chart. Driven by the year/month the admin is viewing, independent of the table's filters.
    // A selected month (chart drill-down) asks the server for that month's daily series (month is 1-based).
    // Hoisted into a useCallback (rather than living inside the effect) so table actions that change what
    // counts as COMPLETED - e.g. cancelling a booking - can re-pull it imperatively. `silent` skips the
    // loading spinner so an action-triggered refresh updates the chart/cards in place without a flash;
    // the year/month effect below keeps the spinner for a deliberate view change.
    const loadSummary = useCallback(async ({ silent = false } = {}) => {
        if (!silent) setSummaryLoading(true);
        try {
            const data = await fetchAdminSummary({
                year: selectedYear,
                month: selectedMonth != null ? selectedMonth + 1 : null,
            });
            setSummary(data);
        }
        catch (err) {
            if (err.response?.status !== 401) {
                showToast("Couldn't load dashboard stats", err.response?.data?.message || "An unexpected error occurred");
            }
        }
        finally {
            if (!silent) setSummaryLoading(false);
        }
    }, [selectedYear, selectedMonth, showToast]);

    useEffect(() => { loadSummary(); }, [loadSummary]);

    // Given to the table so a successful cancel refreshes the chart/stat cards in place (the cancelled
    // booking drops out of the COMPLETED set the summary is built from). Silent = no spinner flash.
    const refreshSummary = useCallback(() => loadSummary({ silent: true }), [loadSummary]);

    // A filter change starts the table back at page 1; the fetch effect below picks it up.
    useEffect(() => { setPage(1); }, [filters.fromDate, filters.toDate, filters.status]);

    // Paginated table rows only.
    useEffect(() => {
        const loadBookings = async () => {
            const data = await load({
                fromDate: filters.fromDate,
                toDate: filters.toDate,
                status: filters.status,
                page,
            });
            if (data) {
                setBookings(data.bookings ?? []);
                setTotalPages(data.totalPages ?? 1);
            }
        };
        loadBookings();
    }, [filters.fromDate, filters.toDate, filters.status, page]);

    const getIncreaseOrDecreaseSign = (current, previous) => {
        if (current > previous) return "+";
        if (previous > current) return "-";
        return "";
    }
    /* Percent change vs last month. Guards the zero-baseline cases:
     *  - last month 0 AND this month 0  -> flat, 0% (NOT 100% - there was no change).
     *  - last month 0 AND this month >0 -> no meaningful base to divide by, so cap at 100%
     *    (paired with the "+" sign it reads as "+100%", i.e. all-new growth, and keeps the
     *    progress ring within its 0-100 range).
     *  - this month 0 AND last month >0 -> the normal formula already gives 100% (a full drop). */
    const percentChange = (current, previous) => {
        if (previous === 0) return current === 0 ? "0.0" : "100.0";
        return ((Math.abs(current - previous) / previous) * 100).toFixed(1);
    };

    const stats = summary?.stats;
    const thisMonthRevenue = stats?.thisMonthRevenue ?? 0;
    const thisMonthBookings = stats?.thisMonthBookings ?? 0;
    const lastMonthRevenue = stats?.lastMonthRevenue ?? 0;
    const lastMonthBookings = stats?.lastMonthBookings ?? 0;

    const revenuePercentage = percentChange(thisMonthRevenue, lastMonthRevenue);
    const revenueIncreaseOrDecrease = getIncreaseOrDecreaseSign(thisMonthRevenue, lastMonthRevenue);
    const bookingPercentage = percentChange(thisMonthBookings, lastMonthBookings);
    const bookingIncreaseOrDecrease = getIncreaseOrDecreaseSign(thisMonthBookings, lastMonthBookings);

    // Years to offer in the graph dropdown - whatever years actually have bookings, falling back to this year.
    const now = new Date();
    const years = summary?.availableYears?.length ? summary.availableYears : [now.getFullYear()];

    /* Chart header total. The server always returns `monthly` (12 buckets for the selected year), so:
     *  - no month selected -> sum every month = the whole selected year's total.
     *  - a month selected   -> that single month's bucket (selectedMonth is a 0-based index into monthly).
     * Follows the Revenue/Bookings toggle: euros for revenue, a plain count (no €) for bookings. */
    const monthlySeries = summary?.monthly ?? [];
    const pickField = (bucket) => (isRevenue ? (bucket?.revenue ?? 0) : (bucket?.count ?? 0));
    const chartTotal = selectedMonth === null
        ? monthlySeries.reduce((sum, b) => sum + pickField(b), 0)
        : pickField(monthlySeries[selectedMonth]);
    const monthNamesFull = ["January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"];
    const chartPeriodLabel = selectedMonth === null ? `${selectedYear}` : `${monthNamesFull[selectedMonth]} ${selectedYear}`;
    const chartTotalText = isRevenue
        ? `€${chartTotal.toLocaleString()}`
        : `${chartTotal} booking${chartTotal === 1 ? "" : "s"}`;



    return (

        <div className="home-page-content">
            <div className="page-header">
                <div className="page-header-text">
                    <h1 className="page-title">Dashboard</h1>
                    <p className="page-subtitle">Welcome to your dashboard</p>
                </div>
            </div>
            <div className="home-page-sections">
                <div className="home-page-stats-grid">
                    <StatCard title={"Revenue"} value={`€${thisMonthRevenue.toLocaleString()}`} increase={`${revenueIncreaseOrDecrease}${revenuePercentage}%`} trend={revenueIncreaseOrDecrease} icon={<EuroIcon />} progress={revenuePercentage} />
                    <StatCard title={"Bookings"} value={thisMonthBookings} increase={`${bookingIncreaseOrDecrease}${bookingPercentage}%`} trend={bookingIncreaseOrDecrease} icon={<BookmarkIcon />} progress={bookingPercentage} />
                </div>
                < div className="home-page-middle-grid">
                    <div className="home-page-card home-page-graph-card">
                        <div className="graph-header">
                            <div className="graph-title">
                                <h3> Performance Analytics</h3>
                                <p>{chartTotalText} in {chartPeriodLabel}</p>
                            </div>
                            <div style={{ display: "flex", gap: "8px", alignItems: "center" }}>
                                <div style={{ position: "relative" }}>
                                    <button
                                        onClick={() => setYearOpen(!yearOpen)}
                                        style={{
                                            padding: "7px 14px",
                                            borderRadius: "0.5rem",
                                            fontSize: "0.8rem",
                                            fontWeight: 700,
                                            background: "var(--primary-500)",
                                            color: "var(--muted-fg)",
                                            border: "none",
                                            cursor: "pointer",
                                            display: "flex",
                                            alignItems: "center",
                                            gap: "4px",
                                        }}
                                    >
                                        <span style={{
                                            marginTop: "1px"
                                        }}>{selectedYear}</span><span
                                            style={{
                                                display: "flex",
                                                transition: "transform 0.2s ease",
                                                transform: yearOpen ? "rotate(90deg)" : "rotate(0deg)",
                                            }}
                                        >
                                            <ChevronRight size={16} />
                                        </span>
                                    </button>
                                    {yearOpen && (
                                        <div className="graph-dropdown-menu"                                            >
                                            {years.map((y) => (
                                                <button className="year-option"
                                                    key={y}
                                                    onClick={() => { setSelectedYear(y); setSelectedMonth(null); setYearOpen(false); }}
                                                    style={{
                                                        fontWeight: y === selectedYear ? 700 : 500,
                                                        background: y === selectedYear ? "var(--primary-500)" : "transparent",
                                                        color: y === selectedYear ? "var(--green-500)" : "var(--muted-fg)",
                                                    }}
                                                >
                                                    {y}
                                                </button>
                                            ))}
                                        </div>
                                    )}
                                </div>
                                <button className={`graph-display-option ${isRevenue ? "active" : ""}`}
                                    onClick={() => setIsRevenue(true)}>Revenue</button>
                                <button className={`graph-display-option ${!isRevenue ? "active" : ""}`} onClick={() => setIsRevenue(false)} >Bookings</button>
                            </div>
                        </div>
                        <div className="linechart-wrapper">
                            {summaryLoading ? (
                                <LoadingSpinner message={"Loading Chart Information"} color="#e0e0e0"/>
                            ) : (
                                <div className="graph-container">
                                    <LineChart
                                        monthly={summary?.monthly}
                                        daily={summary?.daily}
                                        selectedYear={selectedYear}
                                        selectedMonth={selectedMonth}
                                        setSelectedMonth={setSelectedMonth}
                                        showRevenue={isRevenue}
                                    />
                                </div>
                            )}
                        </div>

                    </div>
                </div>
                <div className="bookings-table-container">
                    <BookingsTable bookings={bookings} setBookings={setBookings} filters={filters} resetFilters={resetFilters} applyFilters={applyFilters} needsReviewCount={needsReviewCount} refreshNeedsReviewCount={refreshNeedsReviewCount} refreshSummary={refreshSummary} page={page} totalPages={totalPages} onPageChange={setPage} loading={loading} />
                </div>
            </div>
        </div>
    )
}
export default AdminDashboard;
