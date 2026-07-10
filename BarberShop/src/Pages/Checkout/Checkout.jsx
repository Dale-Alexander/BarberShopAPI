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
    const [paymentMethod, setPaymentMethod] = useState("CASH");
    const { bookingId } = useParams();
    const { data, loading, error } = useFetch(`/api/bookings/checkout/${bookingId}`, false);
    const [bookingDetails, setBookingDetails] = useState(null);
    const [clientSecret, setClientSecret] = useState(null);
    const navigate = useNavigate();
    const [fullName, setFullName] = useState("");
    const [phone, setPhone] = useState("");
    //fullName and phone are declared here because iof they are declared in PaymentForm, then they will be reset when i click on the card paymentMethod(when clientSecret is generated)


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


    if (loading) return <LoadingSpinner message = "Loading Booking Details" color = "#ffffff"/>

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
                    <PaymentForm phone={phone} setPhone={setPhone} fullName={fullName} setFullName={setFullName} bookingId={bookingId} bookingDetails={bookingDetails} clientSecret={clientSecret} setClientSecret={setClientSecret} paymentMethod={paymentMethod} setPaymentMethod={setPaymentMethod} />
                </main>
            </div>
        </ElementsWrapper>
    );
};

export default Checkout;