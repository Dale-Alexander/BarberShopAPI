import { useParams, useNavigate} from "react-router-dom";
import { format } from "date-fns";
import { useContext, useState, useEffect } from "react";
import { usePersistentFilters } from "../../Hooks/UsePersistentFilters.js";
import { BarberBookings as BarberBookingsContext } from "../../Context/BarberContext";
import "./BarberBookings.css";
import { ArrowLeft, UserX, Filter } from "lucide-react";
import LoadingSpinner from "../../Components/LoadingSpinner/LoadingSpinner";
import useFetchBarberBookings from "../../Hooks/UseFetchBarberBookings.js";
import FilterModal from "../AdminDashboard/DashboardComponents/Filter/Filter.jsx";
const BarberBookings = () => {
    const { id } = useParams();
    const { load, loading } = useFetchBarberBookings();
    const { filters, applyFilters, resetFilters } = usePersistentFilters();
    const { viewBookingsBarber } = useContext(BarberBookingsContext);
    const navigate = useNavigate();
   
    /*const { data, loading } = useFetch(`/api/barbers/${id}/bookings`, true);*/
    const [barberBookings, setBarberBookings] = useState(null);
    const [filterModalOpen, setFilterModalOpen] = useState(false);

    useEffect(() => {
        console.log("fromDate", filters.fromDate, "toDate", filters.toDate, "status", filters.status);
        const loadBookings = async () => {
            const data = await load({
                fromDate: filters.fromDate,
                toDate: filters.toDate, 
                status: filters.status,
                barberId:Number(id)
            })
            if (data) setBarberBookings(data);
            console.log("response is", data);
        }
        loadBookings();
    }, [filters.fromDate, filters.toDate, filters.status, id]);

    useEffect(() => {
        console.log(viewBookingsBarber);
    }, [viewBookingsBarber])

    if (loading) {
        return <LoadingSpinner message={"Loading Barber Data"} color="#e0e0e0"/>
    }

    if (!barberBookings) return (
        <div className="barber-error-state">
            <UserX size={48} />
            <h2>Barber Not Found</h2>
            <p>The barber you're looking for doesn't exist.</p>
            <div className = "barber-bookings-top-right">
            <button className="back-to-team" onClick={() => navigate("/admin/team")}>
                <ArrowLeft size={15} /> BACK TO TEAM
            </button>
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
            <div className="barber-bookings-header">
                <div>
                    <h1 className="barber-bookings-title">BOOKINGS</h1>
                    <p className="barber-bookings-subtitle">Viewing bookings for {barberBookings?.barberName} {barberBookings?.barberSurname}</p>
                </div>
                <div className="barber-bookings-top-right">
                    <section aria-label="Filter">
                        <button onClick={() => setFilterModalOpen(true)} className="bookings-admin-table-btn bookings-admin-table-btn-filter-2">
                            <span className="bookings-admin-table-btn-icon"><Filter size={16} /></span>
                            Filter
                        </button>
                    </section>
                <button className="back-to-team" onClick={() => navigate("/admin/team")}>
                    <ArrowLeft size={15}/> BACK TO TEAM
                </button>
                </div>
            </div>

            <div className="barber-bookings-content-area">
                <div className="barber-detail-card">
                    <div className="barber-detail-left">
                        <div className="barber-detail-avatar">
                            <img src={barberBookings?.barberImageUrl?.startsWith("http") ? barberBookings?.barberImageUrl : `${import.meta.env.VITE_BASE_URL}${barberBookings?.barberImageUrl}`} alt={barberBookings?.barberName} />
                            <span className="barber-detail-status" />
                        </div>
                        <div className="barber-detail-meta">
                            <h2 className="barber-detail-name">{barberBookings?.barberName} {barberBookings?.barberSurname}</h2>
                        </div>
                    </div>
                    <div className="barber-detail-stats">
                        <div className="barber-stat">
                            <span className="barber-stat-value">{barberBookings?.bookings?.length}</span>
                            <span className="barber-stat-label">Bookings</span>
                        </div>
                        <div className="barber-stat">
                            <span className="barber-stat-value">
                                {new Set(barberBookings?.bookings?.map((b) => b.userId)).size}
                            </span>
                            <span className="barber-stat-label">Clients</span>
                        </div>
                        <div className="barber-stat">
                            <span className="barber-stat-value">
                                {new Set(barberBookings?.bookings?.flatMap((b) => b.services.map((s) => s.serviceName))).size}
                            </span>
                            <span className="barber-stat-label">Services</span>
                        </div>
                    </div>
                </div>

                {barberBookings?.bookings.length === 0 ? (
                    <p className="no-bookings">No bookings yet for this barber.</p>
                ) : (
                    <div className="bookings-table-wrap">
                        <table className="bookings-table">
                            <thead >
                                <tr className="barber-bookings-table-row">
                                        <th className="barber-bookings-table-header">Client</th>
                                        <th className="barber-bookings-table-header">Phone</th>
                                    <th className="barber-bookings-table-header">Date&Time</th>
                                    <th className="barber-bookings-table-header">Service</th>
                                </tr>
                            </thead>
                            <tbody>
                                {barberBookings?.bookings?.map((b, index) => (
                                    <tr key={index} className= "barber-bookings-table-row">
                                        <td className="barber-bookings-table-data">{b.name} {b.surname}</td>
                                        <td className="barber-bookings-table-data">{ b.phone}</td>
                                        <td className="barber-bookings-table-data">
                                            {format(new Date(b.startDateTime), "dd-MM-yyyy HH:mm")}
                                            {new Date(b.startDateTime) > new Date() ? (
                                                <span className="booking-status upcoming">Upcoming</span>
                                            ): (
                                                    <span className="booking-status fulfilled">Fulfilled</span>
                                            ) }
                                        </td>
                                        <td className="barber-bookings-table-data">
                                            {b.services.map((s, index) => (
                                                <div className="service-row">
                                                    <span className="booking-service-badge" key={index}>{s.serviceName}</span>
                                                    <span className="service-price">€{s.price}</span>
                                                </div>
                                            ))}</td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                )}
            </div>
            {filterModalOpen && (
                <>
                    <FilterModal setFilterModalOpen={setFilterModalOpen} filters={filters} applyFilters={applyFilters} resetFilters={resetFilters} />
                </>
            ) }
        </>
    )
}
export default BarberBookings;