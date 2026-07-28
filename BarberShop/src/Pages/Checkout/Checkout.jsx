import "./Checkout.css";
import { useState, useEffect} from "react";
import { useParams, useNavigate } from "react-router-dom";
import { Scissors, LockKeyhole } from "lucide-react";
import Navlinks from "../../Components/NavLinks/Navlinks";
import useFetch from "../../Hooks/useFetch";
import ElementsWrapper from "./ElementsWrapper/ElementsWrapper.jsx";
import PaymentForm from "./PaymentForm/PaymentForm";
import LoadingSpinner from "../../Components/LoadingSpinner/LoadingSpinner";
const Checkout = () => {
    const { bookingId } = useParams();
    const [paymentMethod, setPaymentMethod] = useState(
        () => sessionStorage.getItem(`clientSecret_${bookingId}`) ? "CARD" : "CASH"
    );
    const { data, loading, error } = useFetch(`/api/bookings/checkout/${bookingId}`, false);
    const [bookingDetails, setBookingDetails] = useState(null);
    const [clientSecret, setClientSecret] = useState(
        () => sessionStorage.getItem(`clientSecret_${bookingId}`) || null
    );
    const navigate = useNavigate();
    // Restored from sessionStorage (see the persist effect below) so a mid-payment refresh - or a bounce
    // back here after a failed redirect payment like Revolut, which fully leaves the site and wipes React
    // state - brings back what the customer already typed instead of resetting the fields to blank.
    const [fullName, setFullName] = useState(() => sessionStorage.getItem(`checkout_name_${bookingId}`) || "");
    const [phone, setPhone] = useState(() => sessionStorage.getItem(`checkout_phone_${bookingId}`) || "");
    const [email, setEmail] = useState(() => sessionStorage.getItem(`checkout_email_${bookingId}`) || "");
    //fullName and phone are declared here because if they are declared in PaymentForm, then they will be reset when i click on the card paymentMethod(when clientSecret is generated)

    useEffect(() => {
        // Persisted so a refresh mid-payment resumes the same PaymentIntent instead of
        // creating a new one every time (startCardFlow's `if (clientSecret) return clientSecret`
        // guard only works against in-memory state, which a reload wipes out).
        if (clientSecret) sessionStorage.setItem(`clientSecret_${bookingId}`, clientSecret);
        else sessionStorage.removeItem(`clientSecret_${bookingId}`);
    }, [clientSecret, bookingId]);

    // Persist the contact fields alongside the clientSecret so they survive a refresh or a failed
    // redirect-payment bounce back to checkout (see the restored useState initializers above). Keyed by
    // bookingId so values never leak into another booking, and cleared on tab close (sessionStorage).
    useEffect(() => {
        const persist = (key, value) =>
            value ? sessionStorage.setItem(`${key}_${bookingId}`, value)
                  : sessionStorage.removeItem(`${key}_${bookingId}`);
        persist("checkout_name", fullName);
        persist("checkout_phone", phone);
        persist("checkout_email", email);
    }, [fullName, phone, email, bookingId]);

    useEffect(() => {
        if (error) {
            const status = error.response?.status;
            if (status === 404) {
                navigate("/404", { replace: true });//you can use any invalid url, the convention is either 404 or not-found
            } else {
                // 400 - non-pending booking
                navigate(`/cancelledorcompleted/${bookingId}`, { replace: true });
            }
        }
            if (!data) return;
            console.log(data);
            if (bookingId) setBookingDetails(data);
        }, [data, bookingId, error]);


    // Match the checkout page's background (.checkout-page) so the loading state doesn't flash white first.
    if (loading) return <LoadingSpinner message = "Loading Booking Details" color = "#000000" fullscreen background="#f9f8f6"/>

    return (
        <ElementsWrapper clientSecret = {clientSecret}>
        <div className="checkout-page">
            {/*HEADER*/}
            <header className="checkout-header">
                <div className="checkout-header__inner">
                    <a href="/booking" className="checkout-header__logo">
                        <div className="checkout-header__logo-icon">
                            <Scissors size={16} color = "white"/> 
                        </div>
                        <span className="checkout-header__logo-text">THE FADE HOUSE</span>
                    </a>
                    <div className="checkout-header__secure">
                        <LockKeyhole size={16}/>
                        <span>Secure Checkout</span>
                    </div>
                </div>
            </header>

            {/* MAIN */}
            <main className="checkout-main">
                    <Navlinks currentScreen="checkout" isEditMode={false} isAdminMode={false} bookingId={bookingId} />
                    <PaymentForm email={email} setEmail={setEmail} phone={phone} setPhone={setPhone} fullName={fullName} setFullName={setFullName} bookingId={bookingId} bookingDetails={bookingDetails} clientSecret={clientSecret} setClientSecret={setClientSecret} paymentMethod={paymentMethod} setPaymentMethod={setPaymentMethod} />
                </main>
            </div>
        </ElementsWrapper>
    );
};

export default Checkout;