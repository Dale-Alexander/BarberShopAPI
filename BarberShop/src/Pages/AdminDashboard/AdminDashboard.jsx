import { useEffect, useState, useContext, useCallback } from "react";
import { adminAxios } from "../../Hooks/AxiosInterceptor.js";
import "./DashboardComponents/StatCard/StatCard";
import BookmarkIcon from '@mui/icons-material/Bookmark';
import AttachMoneyIcon from "@mui/icons-material/AttachMoney";
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
    useEffect(() => {
        const loadSummary = async () => {
            setSummaryLoading(true);
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
                setSummaryLoading(false);
            }
        };
        loadSummary();
    }, [selectedYear, selectedMonth]);

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
    const percentChange = (current, previous) =>
        (previous === 0 ? 100 : (Math.abs(current - previous) / previous) * 100).toFixed(1);

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



    return (

        <div className="home-page-content">
            <div className="home-page-sections">
                <div className="home-page-welcome">
                    <div className="home-page-welcome-text">
                        <h1> Dashboard</h1>
                        <p>Welcome to your dashboard</p>
                    </div>
                </div>
                <div className="home-page-stats-grid">
                    <StatCard title={"Revenue"} value={thisMonthRevenue} increase={`${revenueIncreaseOrDecrease}${revenuePercentage}%`} icon={<AttachMoneyIcon />} progress={revenuePercentage} />
                    <StatCard title={"Bookings"} value={thisMonthBookings} increase={`${bookingIncreaseOrDecrease}${bookingPercentage}%`} icon={<BookmarkIcon />} progress={bookingPercentage} />
                </div>
                < div className="home-page-middle-grid">
                    <div className="home-page-card home-page-graph-card">
                        <div className="graph-header">
                            <div className="graph-title">
                                <h3> Performance Analytics</h3>
                                <p>{thisMonthRevenue}</p>
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
                    <BookingsTable bookings={bookings} setBookings={setBookings} filters={filters} resetFilters={resetFilters} applyFilters={applyFilters} needsReviewCount={needsReviewCount} refreshNeedsReviewCount={refreshNeedsReviewCount} page={page} totalPages={totalPages} onPageChange={setPage} loading={loading} />
                </div>
            </div>
        </div>
    )
}
export default AdminDashboard;
