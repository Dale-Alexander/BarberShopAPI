import "../AlreadyPaid/AlreadyPaid.css";
import {
    Scissors, CreditCard, Star, CalendarDays, Clock, Hash, ShieldCheck, CalendarCheck, House, CheckCheck, LockKeyhole,
    RefreshCw
}
    from "lucide-react";
import useFetch from "../../Hooks/useFetch";
import axios from "axios";
import { useParams, useNavigate } from "react-router-dom";
import { useState, useEffect, useContext, useCallback } from "react";
import LoadingSpinner from "../../Components/LoadingSpinner/LoadingSpinner";
import { getErrorMessage } from "../../utils/errorMessage.js";
import { formatEuro } from "../../utils/money.js";
import { ToastContext } from "../../Context/ToastContext";
/* Shared by the confirmed screen and the waited-out one below: they are the same page in two states, not
   two places, so the chrome has to be identical. Module level, not nested in the component, or React
   would remount it on every render. */
const PageHeader = () => (
    <header className="ap-header">
        <div className="ap-header__inner">
            <a href="/booking" className="ap-header__logo">
                <div className="ap-header__logo-icon">
                    <Scissors size={14} color="white" />
                </div>
                <span className="ap-header__logo-text">THE FADE HOUSE</span>
            </a>
            <div className="ap-header__secure">
                <span className="ap-header__secure-text"><LockKeyhole size={20} /> Secure Checkout</span>
            </div>
        </div>
    </header>
);

const AlreadyPaid = () => {
    const { bookingId } = useParams();
    const [bookingDetails, setBookingDetails] = useState(null);
    // True while we're waiting out the webhook: a card booking is flipped PENDING->COMPLETED by the
    // async webhook, but Stripe redirects here the instant the card succeeds, so the booking can still
    // be PENDING (the endpoint 400s) for a beat. We poll rather than error out - see the effect below.
    const [finalising, setFinalising] = useState(false);
    /* The webhook didn't land inside the window below. NOT an error state: the customer has paid, the
       booking is real, and the shop's side settles it with or without this page. It only changes what we
       show - a message they can walk away from, instead of a spinner that goes nowhere. */
    const [waitedItOut, setWaitedItOut] = useState(false);
    const [checkingAgain, setCheckingAgain] = useState(false);
    const { data, loading, error } = useFetch(`/api/bookings/alreadypaid/${bookingId}`, false);
    const { showToast } = useContext(ToastContext);
    const navigate = useNavigate();

    /* Stripe appends redirect_status to the return_url after a redirect-based payment (e.g. Revolut).
       "failed" means the customer didn't complete it (cancelled in the Revolut app, closed it, or was
       declined) and the booking is still PENDING. Skip the "finalising" poll below (which would otherwise
       spin for ~10s as if a confirmation were coming) and send them straight back to checkout to retry.
       We act ONLY on "failed": a query param is client-controlled, so we never trust "succeeded" to show a
       confirmation - the backend booking status (set by the webhook) stays the source of truth. Acting on
       "failed" only navigates to checkout, which is harmless even if the value were spoofed. */
    const paymentFailed = new URLSearchParams(window.location.search).get("redirect_status") === "failed";
    useEffect(() => {
        if (!paymentFailed) return;
        showToast("Payment not completed", "Your payment wasn't completed. Please try again.", "info");
        navigate(`/checkout/${bookingId}`, { replace: true });
    }, [paymentFailed, bookingId, navigate, showToast]);

    useEffect(() => {
        if (!error) return;
        const status = error.response?.status;
        if (status === 404) {
            navigate("/404", { replace: true });//you can use any invalid url, the convention is either 404 or not-found
            return;
        }
        // 400 = "still pending". Don't toast/blank the page - enter the finalising state and let the
        // poll below wait for the webhook to settle it (or send them to checkout if it never does).
        if (status === 400) {
            setFinalising(true);
            return;
        }
        showToast("Couldn't load booking", getErrorMessage(error, "We couldn't load this booking. Please refresh and try again."));
    }, [error, bookingId, navigate, showToast]);

    useEffect(() => {
        if (!data) return;
        /* The card can succeed but the booking still end up CANCELLED: a closure landed on the slot in the
           payment window, so the webhook refunded and cancelled it (see WebHookController closure guard).
           Stripe still redirects here on success, so without this check we'd wrongly show "Booking Confirmed"
           for a booking that was cancelled and refunded. Send them to the cancelled screen instead. */
        if (data.status === "CANCELLED") {
            navigate(`/cancelledorcompleted/${bookingId}`, { replace: true });
            return;
        }
        setBookingDetails(data);
    }, [bookingId, data, navigate])

    /* One look at the booking. Shared by the wait below and the "Check again" button so both react
       identically. Returns true only when the booking has SETTLED - which is either ending, not just the
       happy one: when the webhook lands it re-checks the slot first, and a closure, a deactivated barber
       or narrowed hours during the payment make it refund and CANCEL instead of confirm. That is why this
       screen never promises a confirmation.

       Every failure counts as "not settled" and nothing more. A 400 is the endpoint saying the booking is
       still PENDING; anything else - a 500, a dropped connection - means we could not READ the booking,
       which tells us nothing about it and is no reason to stop asking. This used to abandon the wait on
       any non-400, so one wifi blip four seconds in ended it; retrying is exactly the right response to a
       transient failure, and the worst case is a handful of requests nobody answers. */
    const checkOnce = useCallback(async () => {
        try {
            const res = await axios.get(`/api/bookings/alreadypaid/${bookingId}`);
            if (res.data?.status === "CANCELLED") {
                navigate(`/cancelledorcompleted/${bookingId}`, { replace: true });
                return true;
            }
            // 200 -> COMPLETED (the endpoint 400s while still pending), so the webhook has landed.
            setBookingDetails(res.data);
            setFinalising(false);
            return true;
        } catch {
            return false;
        }
    }, [bookingId, navigate]);

    /* Waits out the webhook when the booking is still PENDING on arrival (the 400 case above). A card
       payment's PENDING->COMPLETED flip is done by the async webhook, but Stripe redirects here the
       instant the card succeeds, so we can beat it.

       Backing off rather than a flat 2s: delivery is normally well under a second, so the early looks are
       close together to catch the usual case fast, then they spread out because a webhook that hasn't
       arrived in ten seconds is not about to arrive on the next tick either. About 30 seconds in total.

       What happens at the end is the point. This used to send the customer to /checkout - a payment form,
       to somebody Stripe has already told us paid. Even self-correcting (the checkout page bounces a
       non-PENDING booking back out), it invites a second payment for the sake of a webhook that is
       probably seconds away. Now the page just says so and lets them leave. */
    useEffect(() => {
        if (!finalising) return;
        let stopped = false;
        const poll = async () => {
            for (const delay of [2000, 2000, 3000, 4000, 5000, 6000, 8000]) {
                await new Promise((r) => setTimeout(r, delay));
                if (stopped) return;
                const settled = await checkOnce();
                if (stopped || settled) return;
            }
            if (!stopped) setWaitedItOut(true);
        };
        poll();
        return () => { stopped = true; };
    }, [finalising, checkOnce])

    /* There used to be a SECOND poll here, watching an already-COMPLETED booking for a late flip to
       CANCELLED. It was written when a closure cancelled bookings that had already been paid for, and it
       has been guarding nothing since that changed: a closure, a deactivated barber and a narrowed
       schedule now FLAG a confirmed booking and leave it standing, and every call into
       BookingConflictCanceller passes PENDING bookings only. The webhook can't do it either - its
       refund-and-cancel branch runs on the way to COMPLETED, not after it. The only thing left that can
       cancel a confirmed booking is an admin pressing cancel, and catching the 8-second coincidence where
       that happens while the customer reads their receipt did not justify four requests on every single
       confirmed booking. They get the cancellation email regardless.
       The two cases that DO happen are both still covered: cancelled before the page loaded (the effect
       above, straight off the first fetch) and cancelled while the customer waits out the webhook (the
       finalising poll, which is F5). */

    /* The one control on the waited-out screen. A button that silently does nothing would be the worst
       thing to put there - the entire screen exists because the customer can't tell what's happening.

       The message reports the CHECK, not the state. The screen behind it already says the booking isn't
       confirmed and that an email is coming; repeating that would make the button look like it had done
       nothing. What the customer doesn't know is whether it actually looked, and what the answer was just
       now - so that is what this says.

       Left disabled on success: the screen is about to be replaced by the receipt, and re-enabling it
       first would flash a live button on the way out. */
    const handleCheckAgain = async () => {
        if (checkingAgain) return;
        setCheckingAgain(true);
        if (await checkOnce()) return;
        showToast("Checked just now", "Still not confirmed. We'll email you as soon as it's settled.", "info");
        setCheckingAgain(false);
    };

    //early returns after all hooks. Loading after useEffect
    // Redirect payment came back failed - the effect above is navigating to checkout; render a neutral
    // spinner (not the "Finalising"/"Confirmed" UI) so nothing misleading flashes during that beat.
    if (paymentFailed) return <LoadingSpinner message="Redirecting..." color="#000000" fullscreen background="#f9f8f6" />
    // Match the page's background (.ap-page) so the loading state doesn't flash white first.
    if (loading) return <LoadingSpinner message="Loading Booking Details" color="#000000" fullscreen background="#f9f8f6" />
    // Still waiting for the webhook to flip the just-paid card booking to COMPLETED (the 400/pending case).
    /* Names the payment, not just the booking. The customer reaches this screen only after Stripe told us
       the card succeeded (a failure goes down the paymentFailed path above), and the one thing someone
       staring at a spinner after being charged wants to know is whether their money went through. */
    if (finalising && !bookingDetails && !waitedItOut) return <LoadingSpinner message="Payment received - finalising your booking..." color="#000000" fullscreen background="#f9f8f6" />
    /* Waited it out and the webhook still hasn't landed. Written to be walked away from: the money is
       accounted for, the sentence that matters is that nothing here needs them, and the email is the
       thing that will actually reach them. "Settled" rather than "confirmed" because it might not be -
       the same webhook can refund and cancel this booking if the slot went while they were paying, and
       promising a confirmation the shop may not honour is worse than saying nothing.
       Check again is deliberately the only control, and deliberately secondary. There is no "pay again"
       anywhere on this screen: Stripe redirects here only after the card succeeded, so offering to take
       payment again is inviting a double charge to fix a problem the customer doesn't have. */
    if (finalising && !bookingDetails) return (
        <div className="ap-page">
            <PageHeader />
            <main className="ap-main">
                <div className="ap-card-wrapper">
                    {/* The green circle is honest here - the PAYMENT is the part that succeeded, and the
                        card icon keeps it about the money rather than reading as "booking confirmed". */}
                    <div className="ap-icon-circle-2">
                        <CreditCard />
                    </div>
                    <div className="ap-heading">
                        <h2 className="ap-heading__title">Payment received</h2>
                        <p className="ap-heading__subtitle">
                            Your booking still has to be confirmed on our side. That happens on its own — it
                            doesn't need this page open.
                        </p>
                        <p className="ap-heading__subtitle">
                            You can close this page now. We'll email you as soon as it's settled.
                        </p>
                    </div>
                    <div className="ap-actions">
                        <button className="ap-btn ap-btn--outline" onClick={handleCheckAgain} disabled={checkingAgain}>
                            <RefreshCw size={16} /> {checkingAgain ? "Checking..." : "Check again"}
                        </button>
                    </div>
                </div>
            </main>
        </div>
    );
    // Error paths (network / 500) show a toast; render nothing behind it rather than a blank "Confirmed" card.
    if (!bookingDetails) return null;



    return (
        <div className="ap-page" >
            <PageHeader />

            <main className="ap-main">
                <div className="ap-card-wrapper">

                    <div className="ap-icon-circle-2">
                        <CheckCheck/>
                    </div>

                    <div className="ap-heading">
                        <h2 className="ap-heading__title">Booking Confirmed</h2>
                        <p className="ap-heading__subtitle">{bookingDetails?.customerName}, your booking is all set and ready to go</p>
                        <p className="ap-heading__subtitle">We've sent a confirmation to your email — check your inbox (and your spam folder if you don't see it).</p>
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
                                <ShieldCheck size={14} color="#34a853"/>
                                <span style={{ color: "#34a853" }}>
                                    Confirmed
                                </span>
                            </div>
                        </div>

                        <div className="ap-receipt__body">
                            {[
                                { icon: <Scissors size={14} color="#888" />, label: "Barber", value: bookingDetails?.barberName },
                                { icon: <Star size={14} color="#888" />, label: bookingDetails?.serviceNames.length > 1 ? "Services" : "Service", value: bookingDetails?.serviceNames?.join(", ") },
                                { icon: <CalendarDays size={14} color="#888" />, label: "Date", value: bookingDetails?.date },
                                { icon: <Clock size={14} color="#888" />, label: "Time", value: bookingDetails?.time },
                                { icon: <CreditCard size={14} color="#888" />, label: "Payment Method", value: bookingDetails?.paymentMethod },
                                { icon: <Hash size={14} color="#888" />, label: "Booking ID", value: bookingDetails?.bookingId, mono: true },
                            ].map(({ icon, label, value, mono }) => (
                                <div className="ap-receipt__row" key={label}>
                                    <div className="ap-receipt__row-icon">{icon}</div>
                                    <div className="ap-receipt__row-info">
                                        <p className="ap-receipt__row-label">{label}</p>
                                        <p className={`ap-receipt__row-value${mono ? " ap-mono" : ""}`}>{value}</p>
                                    </div>
                                </div>
                            ))}
                        </div>

                            <div className = "ap-receipt__footer">
                                <p className="ap-receipt__footer-label-2">Total</p>
                                <p className="ap-receipt__footer-amount">{formatEuro(bookingDetails?.amountPaid)}</p>
                            </div>
                    </div>


                    <div className="ap-actions">
                        <button className="ap-btn ap-btn--dark" onClick={() => navigate("/datetime")}>
                            <CalendarCheck size={16} /> Book Another Appointment
                        </button>
                        <button className="ap-btn ap-btn--outline" onClick={() => navigate("/")}>
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
