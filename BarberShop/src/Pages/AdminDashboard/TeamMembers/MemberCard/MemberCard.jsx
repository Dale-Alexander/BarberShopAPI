import {Eye, Pencil, Trash2, RotateCcw, CalendarOff, CalendarCheck } from "lucide-react";
import { useContext } from "react";
import { useNavigate } from "react-router-dom";
import "./MemberCard.css";
import { BarberBookings } from "../../../../Context/BarberContext";
import { resolveBarberImage, handleBarberImageError } from "../../../../utils/barberImage.js";
const MemberCard = ({ barber, setDeleteBarberId, onEdit, onReactivate, onToggleBookable, togglingBookable }) => {
    const navigate = useNavigate();
    const {setViewBookingsBarber} = useContext(BarberBookings);
    //this might need to be passed as a prop from TeamMembers.jsx

    /* Deactivated barbers get View + Reactivate only. Edit and Delete are hidden rather than disabled
       because the backend rejects both outright for an inactive row (UpdateBarber filters on isActive,
       DeleteBarber 400s "already inactive") - a fixable name is corrected on the way back in through
       the reactivate modal. Viewing still works: their past bookings are real history. */
    const isActive = barber.isActive;
    /* Separate from isActive: a barber can be live staff but closed to NEW customer bookings while they
       work out their notice. Defaulted to true so a row from an older response shape (or an optimistic
       one built before the field existed) reads as bookable rather than silently badging as winding down. */
    const acceptsNewBookings = barber.acceptsNewBookings !== false;
    const windingDown = isActive && !acceptsNewBookings;

    return (
        <div className={`barber-card${isActive ? "" : " barber-card--inactive"}${windingDown ? " barber-card--winding-down" : ""}`}>
            {!isActive && <span className="barber-card-badge">INACTIVE</span>}
            {windingDown && <span className="barber-card-badge barber-card-badge--winding-down">NO NEW BOOKINGS</span>}
            <div className="barber-card-img">
                <img src={resolveBarberImage(barber.imageUrl)} onError={handleBarberImageError} />
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
                {isActive ? (
                    <>
                        {/* Stops NEW customer bookings without deactivating: they keep their login and
                            their calendar, and honour or cancel what's already booked themselves. Only
                            offered on live barbers - an inactive one is already unbookable. */}
                        <button className="barber-action-btn barber-action-bookable"
                            onClick={() => onToggleBookable(barber)}
                            disabled={togglingBookable}
                            title={acceptsNewBookings
                                ? "Stop new bookings (keeps their existing ones)"
                                : "Start taking new bookings again"}>
                            {acceptsNewBookings ? <CalendarOff size={16} /> : <CalendarCheck size={16} />}
                        </button>
                        <button className="barber-action-btn barber-action-edit"
                            onClick={() => onEdit(barber)}
                            title="Edit Barber">
                            <Pencil size={16} />
                        </button>
                        <button className = "barber-action-btn barber-action-delete"
                        onClick = {() => setDeleteBarberId(barber.id)}
                        title = "Delete Barber">
                            <Trash2 size = {16}/>
                        </button>
                    </>
                ) : (
                    <button className="barber-action-btn barber-action-reactivate"
                        onClick={() => onReactivate(barber)}
                        title="Reactivate Barber">
                        <RotateCcw size={16} />
                    </button>
                )}
            </div>
        </div>
    )
}
export default MemberCard;