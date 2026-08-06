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
import { validateName, validateNameInline, validateEmail } from "../../../utils/validation.js";
import { getErrorMessage } from "../../../utils/errorMessage.js";
import { formatEuro } from "../../../utils/money.js";
const PaymentForm = ({email,setEmail,bookingId, bookingDetails, clientSecret, setClientSecret, paymentMethod, setPaymentMethod, phone, setPhone, fullName, setFullName }) => {
    const stripeRef = useRef(null);
    /* For moving focus to whichever contact field blocked the submit (see focusFirstInvalid). The phone
       one is on a wrapper rather than the input: FancyPhoneInput is shared and doesn't forward a ref, and
       giving it one just for this would change a component other pages render too. */
    const nameRef = useRef(null);
    const emailRef = useRef(null);
    const phoneFieldRef = useRef(null);
    const [loadingPayment, setLoadingPayment] = useState(false);
    // Set when the backend says a card payment for this booking is already in flight (from another tab /
    // session) that we can't render a form for. We keep the customer on CARD and show a wait message.
    const [paymentInProgress, setPaymentInProgress] = useState(false);
    const { showToast } = useContext(ToastContext);
    const [hasClickedConfirm, setHasClickedConfirm] = useState(false);
    // Flipped on the first submit / card-pick attempt. Before that the inline errors stay gentle
    // (quiet on empty fields, name minimum deferred); after, the full rules show inline per field.
    const [attempted, setAttempted] = useState(false);
    const navigate = useNavigate();
    const { clearBooking } = useContext(BookingDetailsContext);
    const { handlePhoneChange, isValid: isPhoneValid } = usePhone(phone);
    useEffect(() => {
        console.log(clientSecret);
    }, [clientSecret]);

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

    // Gates the Confirm button. Uses the inline name check (not the min-length one) so a half-typed name
    // doesn't lock the button. That means a blank or one-character name leaves Confirm ENABLED and the
    // submit is stopped later, by the isContactValid check in the two handlers - which is why those have
    // to put focus on the field rather than just return.
    const isUserDetailsValid = () => {
        if (validateNameInline(fullName)) return false;
        if (validateEmail(email)) return false;
        if (!isPhoneValid()) return false;
        return true;
    };

    // Inline errors. Before the first attempt they stay quiet on empty fields and flag only obvious
    // problems as the customer types (name uses the inline check, no 2-char minimum). Once they've
    // attempted, the full rules apply inline - incl. the name minimum and empty-required fields - so the
    // guidance lives next to each field instead of in a toast.
    const nameError = attempted ? validateName(fullName) : (fullName ? validateNameInline(fullName) : null);
    const emailError = (email || attempted) ? validateEmail(email) : null;
    const phoneError = (phone || attempted) ? (isPhoneValid() ? null : "Please enter a valid phone number") : null;

    // All three contact fields fully valid (name incl. the 2-char minimum). Gates submit after the
    // inline errors have been revealed via setAttempted.
    const isContactValid = () => !validateName(fullName) && !validateEmail(email) && isPhoneValid();

    /* Both Confirm handlers used to just flip `attempted` and return when a contact field was wrong. That
       does reveal the inline error - but it renders next to the field, at the top of the form, while the
       Confirm button is at the bottom: several hundred pixels away with the card form open, more than a
       screen apart on a phone. The submit looked like it did nothing at all, and a screen reader was told
       nothing whatsoever.
       In practice only the name can get this far (the button's own gate has already proved the email and
       phone valid using the same functions), but this walks all three in field order so it stays correct
       if either gate changes. Scroll first, then focus with preventScroll, so the field isn't yanked into
       view twice. */
    const focusFirstInvalid = () => {
        const target =
            validateName(fullName) ? nameRef.current
            : !isPhoneValid() ? phoneFieldRef.current?.querySelector(".fpi-number-input")
            : validateEmail(email) ? emailRef.current
            : null;
        if (!target) return;
        target.scrollIntoView({ behavior: "smooth", block: "center" });
        target.focus({ preventScroll: true });
    };


    const isConfirmValid = () => {
        if (!isUserDetailsValid()) return false;
        const confirmValid = paymentMethod === "CASH" || (paymentMethod === "CARD" && clientSecret);
        return confirmValid;
    }


    const startCardFlow = async () => {//called when user clicks visa or mastercard
        if (clientSecret) return clientSecret; /* Reuse the clientSecret we already have (kept in state and
        restored from sessionStorage on refresh) instead of hitting payment-intent again. Re-calling isn't an
        error - the endpoint void-and-recreates the PaymentIntent - but it's a needless extra call and a new
        PaymentIntent when the existing one is still perfectly usable. */
        try {
            // No amount is sent: the server charges the booking's own service total (guards against a
            // tampered amount). bookingDetails.price is display-only.
            const payload = {
                fullName,
                phone,
                bookingId,
                email,
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
                showToast("Payment in progress", "A payment is already being processed for this booking. Please wait a moment, then refresh.", "info");
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
        setAttempted(true);
        if (!isContactValid()) return focusFirstInvalid();
        setHasClickedConfirm(true);
        try {
            // No amount is sent: the server records the booking's own service total (see above).
            const { data } = await axios.post(`/api/bookings/confirm-cash`, {
                fullName,
                bookingId,
                phone,
                email,
            });
            console.log("Booking confirmed", data);
            clearBooking();
            setClientSecret(null);
            navigate(`/booking/success/${bookingId}`, { replace: true });
        }
        catch (err) {
            console.error(err);
            if (isBookingNoLongerPending(err)) return goToCancelledScreen();
            showToast("Couldn't confirm booking", getErrorMessage(err, "We couldn't confirm your booking and you haven't been charged. Please try again."));
            setHasClickedConfirm(false);
        }
    }

    const handleCardConfirm = async () => {
        if (hasClickedConfirm || !isConfirmValid()) return;
        setAttempted(true);
        if (!isContactValid()) return focusFirstInvalid();
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
        setAttempted(true);
        // Same silent-return problem the Confirm handlers had: this button is never disabled, so a blank
        // name means clicking "Pay Online" reveals an error further up the form and otherwise appears to
        // do nothing - the card form simply never opens. Send focus to the field instead.
        if (!isContactValid()) return focusFirstInvalid();
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
                            {/* aria-invalid + aria-describedby tie the red line below to this box. Nothing
                                changes on screen; without them a screen reader announces "Name, edit text"
                                and never reads the error, because the two elements are only related by
                                sitting near each other. describedBy is left off when there's no error so
                                it can't point at an id that isn't rendered. */}
                            <input ref={nameRef} value={fullName} onChange={(e) => setFullName(e.target.value)} className={`checkout-field__input ${nameError ? "checkout-field__input--invalid" : ""}`} type="text" name="customerName" placeholder="John Smith"
                                aria-invalid={!!nameError || undefined}
                                aria-describedby={nameError ? "checkout-name-error" : undefined} />
                            {nameError && <p id="checkout-name-error" className="checkout-field__error">{nameError}</p>}
                        </div>
                        <div className="checkout-field" ref={phoneFieldRef}>
                            <label className="checkout-field__label">Phone Number</label>
                            <FancyPhoneInput value={phone} onChange={(val, iso2) => handlePhoneChange(val, iso2, setPhone)}
                                invalid={!!phoneError}
                                describedBy={phoneError ? "checkout-phone-error" : undefined}/>
                            {phoneError && <p id="checkout-phone-error" className="checkout-field__error">{phoneError}</p>}
                        </div>
                        <div className="checkout-field">
                            <label className="checkout-field__label">Email</label>
                            <input
                                ref={emailRef}
                                value={email}
                                onChange={(e) => setEmail(e.target.value)}
                                className={`checkout-field__input ${emailError ? "checkout-field__input--invalid" : ""}`}
                                type="email"
                                name="customerEmail"
                                placeholder="john@example.com"
                                aria-invalid={!!emailError || undefined}
                                aria-describedby={emailError ? "checkout-email-error" : undefined} />
                            {emailError && (
                                <p id="checkout-email-error" className = "checkout-field__error">{emailError}</p>
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
                                    showToast("Payment Error", msg || "Your payment couldn't be completed. Please check your card details and try again.")
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
                            <p className="checkout-summary__total-amount">{formatEuro(bookingDetails?.price)}</p>
                        </div>
                    </div>
                </div>
            </div>

        </div>
    );
};
export default PaymentForm;