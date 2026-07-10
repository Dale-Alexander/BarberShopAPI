import { Elements } from "@stripe/react-stripe-js";
import { loadStripe } from "@stripe/stripe-js";
const stripePromise = loadStripe(import.meta.env.VITE_STRIPE_PUBLISHABLE_KEY);//it is outside because it shouldnt be recreated on every render
const ElementsWrapper = ({ clientSecret, children }) => {
    const appearance = {
        theme: 'stripe',
        variables: {
            colorPrimary: '#c9a84c',
            colorBackground: '#ffffff',
            colorText: '#1a1a1a',
            colorTextSecondary: '#555555',
            colorTextPlaceholder: '#bbbbbb',
            colorDanger: '#df1b41',
            fontFamily: "'Inter', sans-serif",
            fontSizeBase: '13px',
            borderRadius: '8px',
            spacingUnit: '4px',
        },
        rules: {
            '.Input': {
                border: '1px solid #ede9e0',
                padding: '10px 12px',
                fontSize: '13px',
                color: '#1a1a1a',
                transition: 'border-color 0.2s',
            },
            '.Input:focus': {
                border: '1px solid #c9a84c',
                boxShadow: 'none',
                outline: 'none',
            },
            '.Input::placeholder': {
                color: '#bbbbbb',
            },
            '.Label': {
                fontSize: '12px',
                fontWeight: '600',
                color: '#555555',
                marginBottom: '6px',
            },
        },
    };
    const options = { appearance, clientSecret, fonts: [{ cssSrc: 'https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600&display=swap' }]};
    
    if (!clientSecret) {
        /* What this whole component does is it preserver PaymentForm, meaning PaymentForm (which is the child passed in Confirmation.jsx) is only rendered once, react never remounts it or unmounts it,
            react never loses internal state. It just displays one singular paymentForm and once the clientSecret is generated it wraps a new parent around it. When clientSecret is not rendered, just render Paymentform but when it is render the PaymentForm inside a new parent(<Elements/>)*/
        return <>{children}</>;
    }
    return (
        <Elements /* stripePromise = your readu-to-use connection to Stripe's payment system.
        Stripe needs a safe area on your page to hande card details(so you dont touch them) That is what Elements does.
        Elements is basically Stripe's secure zone where all payment inputs live.
        Hey i want to charge $10 for lemonade. Stripe creates a paymentIntent  object like this:{
  "id": "pi_3OYtTQK6ZfjP7Wyo1G3JtKZk",
  "amount": 1000,
  "currency": "usd",
  "status": "requires_payment_method",
  "client_secret": "pi_3OYtTQK6ZfjP7Wyo1G3JtKZk_secret_aBc123xyz"
}

The clientSecret is like saying "this code belongs to that exact PaymentIntent". When you pass the clientSecret to <Elements>,
Stripe knows "This payment form belongs to payment intent with id: "pi_3OYtTQK6ZfjP7Wyo1G3JtKZk". The amount should be 10, currency is USD and the user still needs to enter their card details.
Without clientSecret that is like saying "I want to pay" but not saying what youre paying for, how much or which order - Stripe wouldnt know what to charge. The clientSecret solves that
by pointing Stripe to the exact order. Stripe needs clientSecret to know which specific PaymentIntent to process on the frontend
clientSecret here allows Stripe.js to know how much to charge, what currency and what payment methods are allowed. So <Elements> is like saying
"Stripe, here's the payment we are working on. Use this secret to connect the card form to that paymentIntent"
Why clientSecret is needed in <Elements>: Because Elements must know which PaymentIntent it's displaying and confirming. 
Without it Stripe.js doesnt know the amount, the currency, the methods allowed or even what payment it's supposed to confirm. Stripe instenally uses that client secret
to link the data entered in your <PaymentElement/> directly to that specific payment.So, the PaymentElement is not just a form — it’s a live, preconfigured interface tied to the exact payment session that your backend created.
 When you do <Elements>,
Stripe creates an Elements instance - think of it as a controller object that manages all your individual components(like card number, expiry, CVC etc). That instance keeps track
of which payment fields exist,(<PaymentElement/> or <CardElement/>) handles all input validation, encryption etc. Then when you do
const elements = useelements(), it gives you an object that internally looks like:f
{
getElement: fn,    // lets you access PaymentElement, CardElement, etc
submit: fn,        // can trigger validation/submission
update: fn,        // can update appearance or options
...other internal methods
The clientSecret is not passed into confirmPayment({}) — it’s already embedded in the elements(const elements = useElements()) instance when <Elements> was created.
}
When you call: stripe.confirmPayment({ elements });, Stripe looks inside elements to find the paymentElement, extracts the inputted card data, combines that data with the PaymentIntent(via its clientSecret).
"<Elements stripe={stripePromise} options={{ clientSecret }}>" - It is the point where stripe says "Alright, im setting up the secure from for the paymentIntent that belongs to this clientSecret",
from that point onward the <PaymentElement/> is automatically tied to that PaymentIntent

*/
            stripe={stripePromise}
            options={options}>
  
            {children}
        </Elements>
    )
}
export default ElementsWrapper;