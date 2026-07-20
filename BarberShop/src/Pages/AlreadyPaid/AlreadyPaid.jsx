import "./AlreadyPaid.css";
import {
    Scissors, CreditCard, User, Star, CalendarDays, Clock, Hash, ShieldCheck, Info, CalendarCheck, House, Ban, LockKeyhole
}
    from "lucide-react";
import useFetch from "../../Hooks/useFetch";
import { useParams, useNavigate } from "react-router-dom";
import { useState, useEffect, useContext } from "react";
import LoadingSpinner from "../../Components/LoadingSpinner/LoadingSpinner";
import { ToastContext } from "../../Context/ToastContext";
const AlreadyPaid = () => {
    const { bookingId } = useParams();
    const [bookingDetails, setBookingDetails] = useState(null);
    const { data, loading, error } = useFetch(`/api/bookings/alreadypaid/${bookingId}`, false);
    const { showToast } = useContext(ToastContext);
    const navigate = useNavigate();


    useEffect(() => {
        if (error) {
            const status = error.response?.status;
            if (status === 404) {
                navigate("/404", { replace: true });//you can use any invalid url, the convention is either 404 or not-found
            } else {
                showToast("Error Fetching Booking", error?.response?.data?.message || "Something went wrong");
                return;
            }
        }
        if (!data) return;
        setBookingDetails(data);
    }, [bookingId, data, error])
    const isCancelled = bookingDetails?.status === "CANCELLED";

//early returns after all hooks. Loading after useEffect
    // Match the page's background (.ap-page) so the loading state doesn't flash white first.
    if (loading) return <LoadingSpinner message="Loading Booking Details" color="#000000" fullscreen background="#f9f8f6" />



    return(
        <div className = "ap-page" >
            <header className="ap-header">
                <div className="ap-header__inner">
                    <a href="/booking" className="ap-header__logo">
                        <div className="ap-header__logo-icon">
                            <Scissors size={14} color="white" />
                        </div>
                        <span className="ap-header__logo-text">THE FADE HOUSE</span>
                    </a>
                    <div className="ap-header__secure">
                        <span className="ap-header__secure-text"><LockKeyhole size={19} /> Secure Checkout</span>
                    </div>
                </div>
            </header>

            <main className="ap-main">
                <div className="ap-card-wrapper">
                    <div className="ap-badge">
                        <div className="ap-badge__dot"></div>
                        <span className="ap-badge__text">{isCancelled ? "Booking Cancelled" : "Payment Already Received"}</span>
                    </div>

                    <div className="ap-icon-circle">
                        {isCancelled ? <Ban size={36} color="#c9a84c" /> : <CreditCard size={36} color="#c9a84c" />}
                    </div>

                    <div className="ap-heading">
                        <h2 className="ap-heading__title">{isCancelled ? "This Booking Has Been Cancelled" : "This Booking Is Already Paid"}</h2>
                        <p className="ap-heading__subtitle">{isCancelled
                            ? "This appointment was cancelled. If you'd like to make a new booking, please use the button below."
                            : "Payment was completed online for this appointment. No further action is needed."}</p>
                    </div>

                    <div className="ap-receipt">
                        <div className="ap-receipt__header">
                            <div className="ap-receipt__header-logo">
                                <div className="ap-receipt__header-icon">
                                    <Scissors size={12} color="white" />
                                </div>
                                <span className="ap-receipt__header-name">THE FADE HOUSE</span>
                            </div>
                            <div className="ap-receipt__paid-badge">
                                <ShieldCheck size={14} color={isCancelled ? "#e74c3c" : "#34a853"} />
                                <span style={{ color: isCancelled ? "#e74c3c" : "#34a853" }}>
                                    {isCancelled ? "Cancelled" : "Confirmed"}
                                </span>
                            </div>
                        </div>

                        <div className="ap-receipt__body">
                            {[
                                { icon: <User size={14} color="#888" />, label: "Customer", value: bookingDetails?.customerName },
                                { icon: <Scissors size={14} color="#888" />, label: "Barber", value: bookingDetails?.barberName },
                                { icon: <Star size={14} color="#888" />, label: bookingDetails?.serviceNames?.length > 1 ? "Services" : "Service", value: bookingDetails?.serviceNames?.join(", ") },
                                { icon: <CalendarDays size={14} color="#888" />, label: "Date", value: bookingDetails?.date },
                                { icon: <Clock size={14} color="#888" />, label: "Time", value: bookingDetails?.time },
                                { icon: <CreditCard size={14} color="#888" />, label: "Payment Method", value: bookingDetails?.paymentMethod },
                                { icon: <Hash size={14} color="#888" />, label: "Booking ID", value: bookingDetails?.bookingId, mono: true },
                            ]
                            // A booking cancelled while still pending has no customer/payment - drop those blank rows.
                            .filter(({ value }) => value != null && value !== "")
                            .map(({ icon, label, value, mono }) => (
                                <div className="ap-receipt__row" key={label}>
                                    <div className="ap-receipt__row-icon">{icon}</div>
                                    <div className="ap-receipt__row-info">
                                        <p className="ap-receipt__row-label">{label}</p>
                                        <p className={`ap-receipt__row-value${mono ? " ap-mono" : ""}`}>{value}</p>
                                    </div>
                                </div>
                            ))}
                        </div>

                        <div className="ap-receipt__footer">
                            {bookingDetails?.paymentMethod && (
                                <div>
                                    <p className="ap-receipt__footer-label">Amount</p>
                                    <p className="ap-receipt__footer-amount">&euro;{bookingDetails?.amountPaid}</p>
                                </div>
                            )}
                            <div className={`ap-receipt__footer-badge ${isCancelled ? "ap-receipt__footer-badge--cancelled" : "ap-receipt__footer-badge--paid"}`}>
                                <ShieldCheck size={14} color={isCancelled ? "#e74c3c" : "#34a853"} />
                                <span>{isCancelled ? "Booking Cancelled" : "Transaction Complete"}</span>
                            </div>
                        </div>
                    </div>

                    <div className="ap-notice">
                        <Info size={16} color="#c9a84c" className="ap-notice__icon" />
                        <p className="ap-notice__text">
                            If you believe this is a mistake or need to make changes to your booking, please contact us directly at{" "}
                            <a href="tel:+15551234567" className="ap-notice__link">(555) 123-4567</a>.
                        </p>
                    </div>

                    <div className="ap-actions">
                        <button className="ap-btn ap-btn--dark" onClick={() => navigate("/datetime") }>
                            <CalendarCheck size={16} /> Book Another Appointment
                        </button>
                        <button className="ap-btn ap-btn--outline" onClick={() => navigate("/") }>
                            <House size={16} /> Back to Home
                        </button>
                    </div>
                </div>
            </main>
        </div >
    );
}

;

export default AlreadyPaid;
