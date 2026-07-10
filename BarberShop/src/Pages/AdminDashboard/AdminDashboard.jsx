import { useEffect, useState, useContext } from "react";
import "./DashboardComponents/StatCard/StatCard";
import BookmarkIcon from '@mui/icons-material/Bookmark';
import AttachMoneyIcon from "@mui/icons-material/AttachMoney";
import { startOfMonth, endOfMonth, isWithinInterval, subMonths, parseISO } from "date-fns";
import "./AdminDashboard.css";
import { usePersistentFilters } from "../../Hooks/UsePersistentFilters.js";
import StatCard from "./DashboardComponents/StatCard/StatCard";
import LineChart from "./DashboardComponents/LineChart/LineChart";
import LoadingSpinner from "../../Components/LoadingSpinner/LoadingSpinner.jsx";
import BookingsTable from "./DashboardComponents/BookingsTable/BookingsTable.jsx";
import { ChevronRight } from 'lucide-react';
import useFetchBookings from "../../Hooks/UseFetchBookings.js";


const AdminDashboard = () => {
   /* const { data = [], loading, error, reFetch } = useFetch("/api/bookings/admin-filter", true);*/
    const [bookings, setBookings] = useState([]);
    const [selectedYear, setSelectedYear] = useState(new Date().getFullYear());
    const [selectedMonth, setSelectedMonth] = useState(null);
    const [revenueValue, setRevenueValue] = useState(0);
    const [bookingValue, setBookingValue] = useState(0);
    const [bookingIncreaseOrDecrease, setBookingIncreaseOrDecrease] = useState("");
    const [revenueIncreaseOrDecrease, setRevenueIncreaseOrDecrease] = useState("");
    const [bookingPercentage, setBookingPercentage] = useState("");
    const [revenuePercentage, setRevenuePercentage] = useState("");
    const { filters, applyFilters, resetFilters } = usePersistentFilters();
    const [isRevenue, setIsRevenue] = useState(true);
    const [yearOpen, setYearOpen] = useState(false);
    const { load, loading } = useFetchBookings();



    useEffect(() => {
        console.log(filters);
        const loadBookings = async () => {
            const data = await load({
                fromDate: filters.fromDate,
                toDate: filters.toDate,
                status: filters.status
            });

            if ( data) setBookings(data);
            console.log("Data is ", data);
        };
        loadBookings();

        //whenever you call controller.abort(), any request that has signal attached
        //to it gets immediately cancelled and throws CanceledError
    }, [filters.fromDate, filters.toDate, filters.status]);

    const getIncreaseOrDecreaseSign = (current, previous) => {
        if (current > previous) return "+";
        if (previous > current) return "-";
        return "";
    }

    useEffect(() => {
        const now = new Date();
        const calculateStatCardInfo = () => {
            // Month ranges
            const currentMonthStart = startOfMonth(now);
            const currentMonthEnd = endOfMonth(now);

            const lastMonthStart = startOfMonth(subMonths(now, 1));
            const lastMonthEnd = endOfMonth(subMonths(now, 1));

            // Initialize counters
            let thisMonthTotalBookings = 0;
            let thisMonthTotalRevenue = 0;
            let lastMonthTotalBookings = 0;
            let lastMonthTotalRevenue = 0;

            // Single pass over data
            bookings.forEach(b => {
                const date = parseISO(b.startDateTime);

                if (isWithinInterval(date, { start: currentMonthStart, end: currentMonthEnd })) {
                    thisMonthTotalBookings += 1;
                    thisMonthTotalRevenue += b.amount;
                } else if (isWithinInterval(date, { start: lastMonthStart, end: lastMonthEnd })) {
                    lastMonthTotalBookings += 1;
                    lastMonthTotalRevenue += b.amount;
                }
            });

            // Update state
            setBookingValue(thisMonthTotalBookings);
            setRevenueValue(thisMonthTotalRevenue);

            /* Calculating Percentages*/
            const RevenuePercentage = lastMonthTotalRevenue === 0
                ? 100
                : (Math.abs(thisMonthTotalRevenue - lastMonthTotalRevenue) / lastMonthTotalRevenue) * 100;
            setRevenuePercentage(RevenuePercentage.toFixed(1));
            setRevenueIncreaseOrDecrease(getIncreaseOrDecreaseSign(thisMonthTotalRevenue, lastMonthTotalRevenue));


            const BookingPercentage = lastMonthTotalBookings === 0
                ? 100
                : (Math.abs(thisMonthTotalBookings - lastMonthTotalBookings) / lastMonthTotalBookings) * 100;
            setBookingPercentage(BookingPercentage.toFixed(1));
            setBookingIncreaseOrDecrease(getIncreaseOrDecreaseSign(thisMonthTotalBookings, lastMonthTotalBookings));
        }
        calculateStatCardInfo();
    }, [bookings])

    /* calculate which years to display to the admin so he can filter his graph data accordingly */
    const now = new Date();
    let years = [
        now.getFullYear(),
    ]
    if (now.getMonth() >= 9) {
        years.push(now.getFullYear() + 1);
    }



    return (

        <div className="home-page-content">
            {/* isLoading ? (
                    <LoadingSpinner/>
                    ) :(*/}
            <div className="home-page-sections">
                <div className="home-page-welcome">
                    <div className="home-page-welcome-text">
                        <h1> Dashboard</h1>
                        <p>Welcome to your dashboard</p>
                    </div>
                </div>
                <div className="home-page-stats-grid">
                    <StatCard title={"Revenue"} value={revenueValue} increase={`${revenueIncreaseOrDecrease}${revenuePercentage}%`} icon={<AttachMoneyIcon />} progress={revenuePercentage} />
                    <StatCard title={"Bookings"} value={bookingValue} increase={`${bookingIncreaseOrDecrease}${bookingPercentage}%`} icon={<BookmarkIcon />} progress={bookingPercentage} />
                </div>
                < div className="home-page-middle-grid">
                    <div className="home-page-card home-page-graph-card">
                        <div className="graph-header">
                            <div className="graph-title">
                                <h3> Performance Analytics</h3>
                                <p>{revenueValue}</p>
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
                                                    onClick={() => { setSelectedYear(y); setYearOpen(false); }}
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
                            {loading ? (
                                <LoadingSpinner message={"Loading Chart Information"} color="#e0e0e0"/>
                            ) : (
                                <div className="graph-container">
                                    <LineChart data={bookings} loading={loading} selectedYear={selectedYear} selectedMonth={selectedMonth} setSelectedMonth={setSelectedMonth} showRevenue={isRevenue} />
                                </div>
                            )}
                        </div>

                    </div>
                </div>
                <div className="bookings-table-container">
                    <BookingsTable bookings={bookings} setBookings={setBookings} filters={filters} resetFilters={resetFilters} applyFilters={applyFilters} />
                </div>
            </div>
        </div>
    )
}
export default AdminDashboard;