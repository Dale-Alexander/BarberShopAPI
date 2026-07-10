import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { SquarePen, Trash2, Plus, Filter, Search } from "lucide-react";
import { format } from "date-fns";
import FilterModal from "../Filter/Filter.jsx";
import "./BookingsTable.css";
import { adminAxios } from "../../../../Hooks/AxiosInterceptor";
const BookingsTable = ({ bookings,setBookings, resetFilters, applyFilters, filters}) => {
    const [openMenuId, setOpenMenuId] = useState(null);
    const [searchInput, setSearchInput] = useState("");
    const [filterModalOpen, setFilterModalOpen] = useState(false);
    const navigate = useNavigate();
    useEffect(() => {
        const handleClickOutside = () => {
            setOpenMenuId(null);
        }
        if (openMenuId != null) {
            document.addEventListener("click", handleClickOutside);
        }
        return () => document.removeEventListener("click", handleClickOutside);
    }, [openMenuId])

    const onCancel = async (bookingId) => {
        try {
            await adminAxios.patch(`/api/bookings/cancel/${bookingId}`);
            setBookings(prev => prev.filter(b => b.id !== bookingId));
        }
        catch (err) {
            console.error(err.response?.data?.message || err.response?.data);
        }
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
                                                    onCancel(b.id); setOpenMenuId(null); 
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
        </div>
    )
}
export default BookingsTable;