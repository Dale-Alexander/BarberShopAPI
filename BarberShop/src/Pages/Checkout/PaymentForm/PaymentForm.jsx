import { useState, useContext, useEffect, useRef } from "react";
import axios from "axios";
import { useNavigate } from "react-router-dom";
import LoadingSpinner from "../../../Components/LoadingSpinner/LoadingSpinner";
import { CalendarCheck,CreditCard, Store, CheckCircle2, ShieldCheck, Clock, CalendarDays, Timer, Scissors } from "lucide-react";
import { ToastContext } from "../../../Context/ToastContext";
import StripePaymentSection from "../StripePaymentSection/StripePaymentSection";
import { BookingDetailsContext } from "../../../Context/BookingDetailsContext";
import FancyPhoneInput from "../../../Components/FancyPhoneInput/FancyPhoneInput";
import usePhone from "../../../Hooks/usePhone";
const PaymentForm = ({ bookingId, bookingDetails, clientSecret, setClientSecret, paymentMethod, setPaymentMethod, phone, setPhone, fullName, setFullName }) => {
    const stripeRef = useRef(null);
    const [loadingPayment, setLoadingPayment] = useState(false);
    const { showToast } = useContext(ToastContext);
    const [hasClickedConfirm, setHasClickedConfirm] = useState(false);
    const navigate = useNavigate();
    const { clearBooking } = useContext(BookingDetailsContext);
    const {handlePhoneChange, isValid:isPhoneValid} = usePhone(phone);
    useEffect(() => {
        console.log(clientSecret);
    }, [clientSecret]);


    const isUserDetailsValid = () => {
        if (!fullName.trim()) return false;
        if (!isPhoneValid()) return false;
        return true;
    };


    const isConfirmValid = () => {
        if (!isUserDetailsValid()) return false;
        const confirmValid = paymentMethod === "CASH" || (paymentMethod === "CARD" && clientSecret);
        return confirmValid;
    }


    const startCardFlow = async () => {//called when user clicks visa or mastercard
        if (clientSecret) return clientSecret; /* This is so if the user tries to confirm a booking but something went wrong and it didnt create a payment record
        and navigates back to checkout from /booking/success he gets to r etry. Without this, if he navigated back and clicked the Card option, that would attempt
        to create another paymentIntent/clientSecret which would fail because an idempotencyKey with that bookingId was already created*/
        try {
            const payload = {
                fullName,
                phone,
                bookingId,
                amount: bookingDetails?.price * 100
            }
            const { data } = await axios.post(`/api/bookings/payment-intent`, payload);
            console.log("Received clientSecret:", data.clientSecret);
            setClientSecret(data.clientSecret);
            return data.clientSecret;
        }
        catch (err) {
            if (err.response?.status === 409) console.log(err.response?.data?.message);
            console.error(err);
        }
    }

    const handleCashConfirm = async () => {
        if (hasClickedConfirm || !isConfirmValid()) return;
        setHasClickedConfirm(true);
        try {
            const { data } = await axios.post(`/api/bookings/confirm-cash`, {
                fullName,
                bookingId,
                phone,
                amount: bookingDetails?.price
            });
            console.log("Booking confirmed", data);
            clearBooking();
            navigate(`/booking/success/${bookingId}`, { replace: true });
        }
        catch (err) {
            console.error(err);
            showToast("Error Confirming Payment", err.response?.data?.message);
            setHasClickedConfirm(false);
        }
    }

    const handleCardConfirm = async () => {
        if (hasClickedConfirm || !isConfirmValid()) return;
        if (!stripeRef.current?.isReady()){
            showToast("Stripe not ready", "Payment Provider isn't ready yet");
            return;
        }
        setHasClickedConfirm(true);
        const success = await stripeRef.current.confirmPayment();
if (success) clearBooking();
else setHasClickedConfirm(false);
    }


    const handleCardMethodSelected = async () => {
        if (!isUserDetailsValid()) {
            showToast("Incorrect Inputs", "Please input a valid name and phone number");
            return;
        }
        setPaymentMethod("CARD");
        setLoadingPayment(true);
        const secret = await startCardFlow();
        if (!secret) {
            setPaymentMethod("CASH");
            showToast("Unexpected error", "Error setting up payment form");
        }
        setLoadingPayment(false);
    }


    return (
        <div className="checkout-grid">
            {/* FORM */}
            <form onSubmit={(e) => { e.preventDefault(); paymentMethod === "CARD" ? handleCardConfirm() : handleCashConfirm(); } } id="checkout-form" className="checkout-form">
                {/*Contact Information*/}
                <div className="checkout-card">
                    <h2 className="checkout-card__title">Contact Information</h2>
                    <p className="checkout-card__subtitle">We'll use this to confirm your appointment</p>
                    <div className="checkout-field-group">
                        <div className="checkout-field">
                            <label className="checkout-field__label">Name</label>
                            <input value={fullName} onChange={(e) => setFullName(e.target.value)} className="checkout-field__input" type="text" name="customerName" placeholder="John Smith" />
                        </div>
                        <div className="checkout-field">
                            <label className="checkout-field__label">Phone Number</label>
                            <FancyPhoneInput value={phone} onChange={(val, iso2) => handlePhoneChange(val, iso2, setPhone)}/>
                        </div>
                    </div>
                </div>

                {/* Payment Method */}
                <div className="checkout-card">
                    <h2 className="checkout-card__title">Payment Method</h2>
                    <p className="checkout-card__subtitle">Choose how you'd like to pay</p>

                    <div className="checkout-payment-methods">
                        <button type="button" className={`checkout-payment-btn ${paymentMethod === "CASH" ? "checkout-payment-btn--selected" : ""}`} onClick={() => setPaymentMethod("CASH")}>
                            <div className="checkout-payment-btn__icon">
                                <Store size={16} />
                            </div>
                            <p className="checkout-payment-btn__label">Pay at Store</p>
                            <p className="checkout-payment-btn__desc">Cash or card on arrival</p>
                            <div className="checkout-payment-btn__badge">
                                {paymentMethod === "CASH" && (
                                    <>
                                        <CheckCircle2 size={16} />
                                        <span>Selected</span>
                                    </>
                                )}
                            </div>
                        </button>
                        <button type="button" onClick={() => handleCardMethodSelected()} className={`checkout-payment-btn ${paymentMethod === "CARD" ? "checkout-payment-btn--selected" : ""}`}>
                            <div className="checkout-payment-btn__icon">
                                <CreditCard size={16} />
                            </div>
                            <p className="checkout-payment-btn__label">Pay Online</p>
                            <p className="checkout-payment-btn__desc">Secure card payment now</p>
                            <div className="checkout-payment-btn__badge">
                                {paymentMethod === "CARD" && (
                                    <>
                                        <CheckCircle2 size={16} />
                                        <span>Selected</span>
                                    </>
                                )}
                            </div>
                        </button>
                    </div>
                    {paymentMethod === "CARD" && (
                        <div className="checkout-card-fields">

                            <div className="checkout-ssl-notice">
                                <ShieldCheck size={16} />
                                <span>256-bit SSL encrypted - your card info is safe</span>
                            </div>
                            {clientSecret && !loadingPayment ? (
                                <StripePaymentSection ref={stripeRef} bookingId={bookingId} onPaymentError={(msg) => 
                                    showToast("Payment Error", msg)
                                }/>
                            ) : (
<LoadingSpinner message="Loading Payment Form" color="#000000" inline />
                            )}
                            
                        </div>
                    )}
                </div>

                <button type="submit" disabled={!isConfirmValid() || hasClickedConfirm} className="checkout-submit-btn">
                    <CalendarCheck size={16} /> Confirm Booking
                </button>

            </form>

            {/* BOOKING SUMMARY */}
            <div className="checkout-summary">
                <div className="checkout-summary__card">
                    <p className="checkout-summary__eyebrow">Booking Summary</p>

                    <div className="checkout-summary__barber">
                        <img className="checkout-summary__barber-img" src={bookingDetails?.imageUrl.startsWith("http") ? bookingDetails?.imageUrl : `${import.meta.env.VITE_BASE_URL}${bookingDetails?.imageUrl}`} />

                        <div>
                            <p className="checkout-summary__barber-name">{bookingDetails?.barberName}</p>
                        </div>
                    </div>

                    <div className="checkout-summary__rows">
                        {[
                            {
                                icon: <Scissors size={16} />,
                                key: "Service",
                                val: bookingDetails?.serviceNames?.join(", ") ?? "Not selected yet"
                            },
                            {
                                icon: <CalendarDays size={16} />,
                                key: "Date",
                                val: bookingDetails?.startDateTime
                                    ? new Date(bookingDetails.startDateTime).toLocaleDateString("en-US", { weekday: "long", month: "short", day: "numeric" })
                                    : "—"
                            },
                            {
                                icon: <Clock size={16} />,
                                key: "Time",
                                val: bookingDetails?.startDateTime
                                    ? new Date(bookingDetails.startDateTime).toLocaleTimeString("en-US", { hour: "2-digit", minute: "2-digit" })
                                    : "—"
                            },
                            {
                                icon: <Timer size={16} />,
                                key: "Duration",
                                val: bookingDetails?.durationMin ? `${bookingDetails.durationMin} min` : "—"
                            },
                        ].map(({ icon, key, val }) => (
                            <div className="checkout-summary__row" key={key}>
                                <div className="checkout-summary__row-icon">
                                    {icon}
                                </div>
                                <div className="checkout-summary__row-text">
                                    <p className="checkout-summary__row-key">{key}</p>
                                    <p className="checkout-summary__row-val">{val}</p>
                                </div>
                            </div>
                        ))}
                    </div>

                    <div>
                        <div className="checkout-summary__total">
                            <p className="checkout-summary__total-key">Total</p>
                            <p className="checkout-summary__total-amount">&euro;{bookingDetails?.price}</p>
                        </div>
                    </div>
                </div>
            </div>

        </div>
    );
};
export default PaymentForm;