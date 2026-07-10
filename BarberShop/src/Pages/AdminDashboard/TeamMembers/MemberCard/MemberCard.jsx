import {Eye, Trash2 } from "lucide-react";
import { useContext } from "react";
import { useNavigate } from "react-router-dom";
import "./MemberCard.css";
import { BarberBookings } from "../../../../Context/BarberContext";
const MemberCard = ({ barber, setDeleteBarberId }) => {
    const navigate = useNavigate();
    const {setViewBookingsBarber} = useContext(BarberBookings);
    //this might need to be passed as a prop from TeamMembers.jsx

    return (
        <div className="barber-card">
            <div className="barber-card-img">
                <img src={barber.imageUrl.startsWith("http") ? barber.imageUrl : `${import.meta.env.VITE_BASE_URL}${barber.imageUrl}`} />
            </div>
            <div className="barber-card-info">
                <h3 className="barber-card-name">{barber.firstName} {barber.lastName}</h3>
                <p className="barber-card-bookings-count">
                    {barber?.totalBookings} booking{barber?.totalBookings !== 1 ? "s" : ""}
                </p>
            </div>
            <div className="barber-card-actions">
                <button className="barber-action-btn barber-action-view"
                    onClick={() => {
                        setViewBookingsBarber(barber);
                        navigate(`/admin/team/${barber.id}`);
                    }}
                    title="View Bookings">
                    <Eye size={16} />
                </button>
                <button className = "barber-action-btn barber-action-delete"
                onClick = {() => setDeleteBarberId(barber.id)}
                title = "Delete Barber">
                    <Trash2 size = {16}/>
                </button>
            </div>
        </div>
    )
}
export default MemberCard;