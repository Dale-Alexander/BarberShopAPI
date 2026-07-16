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
import { resolveBarberImage, handleBarberImageError } from "../../../utils/barberImage.js";
const PaymentForm = ({email,setEmail,bookingId, bookingDetails, clientSecret, setClientSecret, paymentMethod, setPaymentMethod, phone, setPhone, fullName, setFullName }) => {
    const stripeRef = useRef(null);
    const [loadingPayment, setLoadingPayment] = useState(false);
    // Set when the backend says a card payment for this booking is already in flight (from another tab /
    // session) that we can't render a form for. We keep the customer on CARD and show a wait message.
    const [paymentInProgress, setPaymentInProgress] = useState(false);
    const { showToast } = useContext(ToastContext);
    const [hasClickedConfirm, setHasClickedConfirm] = useState(false);
    const navigate = useNavigate();
    const [emailTouched, setEmailTouched] = useState(false);
    const { clearBooking } = useContext(BookingDetailsContext);
    const { handlePhoneChange, isValid: isPhoneValid } = usePhone(phone);
    useEffect(() => {
        console.log(clientSecret);
    }, [clientSecret]);

    const isValidEmail = (value) => {
        if (!value?.trim()) return false;
        return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value.trim());
    }

    /* The slot can vanish from under the customer while they sit on this page: the 15-min hold expires
       (cron) or an admin closes the slot. Both cancel the still-PENDING booking on the server, so the
       backend starts rejecting confirm/pay with a 400. Instead of a cryptic toast, send them to the
       cancelled screen (which offers "Book Another Appointment"). clearBooking() drops the stale hold. */
    const goToCancelledScreen = () => {
        clearBooking();
        setClientSecret(null);
        navigate(`/cancelledorcompleted/${bookingId}`, { replace: true });
    };

    /* True when a 400 means the booking itself is no longer usable (expired hold, cancelled, or the slot
       was closed) rather than a fixable input problem - matches the server messages from confirm-cash and
       payment-intent ("Only Pending Bookings...", "no longer be paid for", "shop closure", "not found"). */
    const isBookingNoLongerPending = (err) => {
        if (err.response?.status !== 400) return false;
        const msg = (err.response?.data?.message || "").toLowerCase();
        return /no longer|pending|closure|not found/.test(msg);
        //the reason we check for the message and not the status is because the backend returns 400 for both "booking is gone" and "your input is valid".
        //We only want the to redirect for the "gone" ones. For an invalid email, the user should stay on the page and fix it. 
    };

    const isUserDetailsValid = () => {
        if (!fullName.trim()) return false;
        if (!isValidEmail(email)) return false;
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
                email,
                amount: bookingDetails?.price * 100
            }
            const { data } = await axios.post(`/api/bookings/payment-intent`, payload);
            setClientSecret(data.clientSecret);
            setPaymentInProgress(false);
            return data.clientSecret;
        }
        catch (err) {
            // Booking is gone (expired / cancelled / slot closed) - redirect; nothing to revert, we're leaving.
            if (isBookingNoLongerPending(err)) {
                goToCancelledScreen();
                return null;
            }
            /* The backend returns 400 with this message when a previous PaymentIntent for this booking is
               still being processed and couldn't be voided. A card payment is genuinely in flight, so we keep
               the customer on CARD (flipping to cash would invite a second, cash payment on top of it). We
               can't render a form for that other PaymentIntent, so show a "please wait" state instead. */
            const message = err.response?.data?.message || "";
            if (/already being processed/i.test(message)) {
                setPaymentInProgress(true);
                showToast("Payment in progress", "A payment is already being processed for this booking. Please wait a moment, then refresh.");
            } else {
                // Genuine setup failure with no payment in flight - drop back to cash so the customer isn't
                // stranded on a card form that can't load.
                console.error(err);
                setPaymentMethod("CASH");
                showToast("Unexpected error", "Something went wrong setting up the payment form. Please try again.");
            }
            return null;
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
                email,
                amount: bookingDetails?.price
            });
            console.log("Booking confirmed", data);
            clearBooking();
            setClientSecret(null);
            navigate(`/booking/success/${bookingId}`, { replace: true });
        }
        catch (err) {
            console.error(err);
            if (isBookingNoLongerPending(err)) return goToCancelledScreen();
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
        if (success) {
            clearBooking();
            setClientSecret(null);
            return;
        }
        /* Payment failed. That's usually just a declined card (booking still PENDING - let them retry),
           but it's also what happens when the hold expired or the slot was closed, which voids the
           PaymentIntent server-side. Re-check the booking: if it's no longer pending, send them to the
           cancelled screen rather than leaving them stuck on a form they can never submit. */
        try {
            await axios.get(`/api/bookings/checkout/${bookingId}`);
            setHasClickedConfirm(false); // still payable - the payment error was already toasted, allow retry
        }
        catch {
            goToCancelledScreen();
        }
    }


    const handleCardMethodSelected = async () => {
        if (!isUserDetailsValid()) {
            showToast("Incorrect Inputs", "Please input a valid name and phone number and email address");
            return;
        }
        setPaymentMethod("CARD");
        setPaymentInProgress(false);
        setLoadingPayment(true);
        // startCardFlow owns the outcome: it renders the form on success, redirects if the booking is gone,
        // keeps CARD + a wait message if a payment is already in flight, or falls back to cash on a hard error.
        await startCardFlow();
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
                        <div className="checkout-field">
                            <label className="checkout-field__label">Email</label>
                            <input
                                value={email}
                                onBlur={() => setEmailTouched(true)}
                                onChange={(e) => setEmail(e.target.value)}
                                className={`checkout-field__input ${emailTouched && !isValidEmail(email) ? "checkout-field__input--invalid" : ""}`}
                                type="email"
                                name="customerEmail"
                                placeholder="john@example.com" />
                            {emailTouched && !isValidEmail(email) && (
                                <p className = "checkout-field__error">Please enter a valid email address</p>
                            ) }
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
                            ) : paymentInProgress ? (/* ▎ The customer opens the checkout for a booking in a different browser or tab than the one where they already started a card payment — and that earlier payment is already going through (processing or completed) on Stripe's side.

In plain terms: they tried to pay by card somewhere else, that payment is already in motion, and now they've come back on a fresh device/tab that doesn't remember it. */
                                <p className="checkout-card__subtitle">A payment is already being processed for this booking. Please wait a moment, then refresh the page to continue.</p>
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
                        <img className="checkout-summary__barber-img" src={resolveBarberImage(bookingDetails?.imageUrl)} onError={handleBarberImageError} />

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
                                    : "�"
                            },
                            {
                                icon: <Clock size={16} />,
                                key: "Time",
                                val: bookingDetails?.startDateTime
                                    ? new Date(bookingDetails.startDateTime).toLocaleTimeString("en-US", { hour: "2-digit", minute: "2-digit" })
                                    : "�"
                            },
                            {
                                icon: <Timer size={16} />,
                                key: "Duration",
                                val: bookingDetails?.durationMin ? `${bookingDetails.durationMin} min` : "�"
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