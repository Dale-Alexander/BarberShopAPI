import { PaymentElement, useStripe, useElements } from "@stripe/react-stripe-js";
import { forwardRef, useImperativeHandle } from "react";
/*
What this component does: The problem was that useStripe() and useElements() must be called inside <Elements>, but PaymentForm needs to trigger confirmPayment on form submit. These two things are in different components.
StripePaymentSection solves this by:

Living inside <Elements> so it can safely call useStripe() and useElements()
Exposing confirmPayment() and isReady() upward to PaymentForm via the ref
Rendering <PaymentElement /> — the actual card input UI

asically this component is used to render useStripe() and useElements() inside
<Elements>. Remmeber that when clientSecret doesnt exist we render<>children</> without the <Elements> wrapped around the children.
Also StripePaymentSection is rendered conditionally in PaymentForm only when clientSecret exists
*/

const StripePaymentSection = forwardRef(({ bookingId, onPaymentError }, ref) => {
    /* Normally when a parent component has a ref on a child, React doesnt
    pass that ref into a child component through props because otherwise the ref is lost and 
    the parent cant access anything. forwardRef tells React pass the ref through so i can use it
    
    useImperativeHandle:
    This lets you control what the parent sees when it uses the ref. Instead of 
    the parent getting access to the raw DOM node, you define a custom object of 
    functions the parent can call. In this case the functions are confirmPayment() and isReady()
    */
    const stripe = useStripe();
    const elements = useElements();
    useImperativeHandle(ref, () => ({
        confirmPayment: async () => {
            if (!stripe || !elements) return false;
            const { error, paymentIntent } = await stripe.confirmPayment({
                elements,
                confirmParams: {
                    return_url: `${window.location.origin}/booking/success/${bookingId}`
                },
                redirect: "if_required"
            });
            if (error) {
                onPaymentError(error.message);
                return false;
            }

            if (paymentIntent?.status === "succeeded") {
                window.location.href = `${window.location.origin}/booking/success/${bookingId}`;
                return true;
            }

            if (paymentIntent?.status === "requires_action") {
                onPaymentError("Further authentication is required. Please follow the instructions.");
                return false;
            }

            if (paymentIntent?.status === "processing") {
                onPaymentError("Your payment is still processing. Please wait.");
                return false;
            }

            // any other unexpected status
            onPaymentError(`Unexpected payment status: ${paymentIntent?.status}. Please contact support.`);
            return false;
        },
        isReady: () => !!stripe && !!elements
    }));
    return <PaymentElement />;
});
export default StripePaymentSection;