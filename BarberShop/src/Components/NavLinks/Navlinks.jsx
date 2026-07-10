import "./Navlinks.css";
import { useNavigate } from "react-router-dom";
const Navlinks = ({ isAdminMode, isEditMode, currentScreen, bookingId }) => {
    const navigate = useNavigate();
    return (
        <nav className="stepper" aria-label="Booking steps">
            <div className="step-item">
                <button onClick={() => navigate("/datetime")} className={`step-btn ${currentScreen === "barberdatetime" ? "active" : "inactive"}`} aria-current="step"
                    disabled={isAdminMode || isEditMode || currentScreen === "barberdatetime"}>
                    <span className="step-badge" aria-hidden="true">2</span>
                    <span className="step-label">Date, Time & Barber</span>
                </button>
            </div>
            <div className="step-connector" role="presentation"></div>
            <div className="step-item">
                <button onClick={() => navigate(`/checkout/${bookingId}`)} className={`step-btn ${currentScreen === "checkout" ? "active" : "inactive"}`} disabled={!bookingId || currentScreen === "checkout"}>
                    <span className="step-badge" aria-hidden="true">3</span>
                            <span className="step-label">{isAdminMode ? "User's Details" : "Your Details"}</span>
                </button>
                    </div>
        </nav>
    )
}
export default Navlinks;