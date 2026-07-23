import { useState, useEffect } from "react";
import "./ForgotPassword.css";
import axios from "axios";
import { PulseLoader } from "react-spinners";
import { Mail } from "lucide-react";
import { validateEmail } from "../../utils/validation";

// Matches the backend's per-email send throttle (RequestPasswordReset), so "Resend" only re-enables
// once a fresh email would actually be sent rather than being silently throttled.
const RESEND_SECONDS = 120;

// Step 1 of the password reset flow: just collects an email and asks the backend to
// send a reset link. This page never sees or sets a new password - that only happens
// on ResetPassword.jsx, reached via the link in the email that RequestPasswordReset
// (authController.cs) enqueues.
const ForgotPassword = () => {
    const [email, setEmail] = useState("");
    const [submitted, setSubmitted] = useState(false);
    const [submitting, setSubmitting] = useState(false);
    const [secondsLeft, setSecondsLeft] = useState(0);

    // Shown live once the user starts typing; the button stays locked until the address is well-formed.
    const emailError = email ? validateEmail(email) : null;

    // Tick the resend cooldown down to zero.
    useEffect(() => {
        if (secondsLeft <= 0) return;
        const t = setTimeout(() => setSecondsLeft((s) => s - 1), 1000);
        return () => clearTimeout(t);
    }, [secondsLeft]);

    // Shared by the first submit and the "Resend" button - the email is already in state either way.
    const sendResetLink = async () => {
        if (validateEmail(email) || submitting || secondsLeft > 0) return; // safety net; UI already gates these
        setSubmitting(true);
        try {
            await axios.post("/api/auth/forgot-password", { email });
        }
        finally {
            // Always show the same confirmation regardless of outcome - the backend intentionally never
            // reveals whether the email exists, so the UI shouldn't either. Start the resend cooldown so
            // the "Resend" button lines up with the backend's per-email throttle.
            setSubmitting(false);
            setSubmitted(true);
            setSecondsLeft(RESEND_SECONDS);
        }
    };

    const handleSubmit = (e) => {
        e.preventDefault();
        sendResetLink();
    };

    return (
        <div className="login-page">
            <div className="forgot-card">
                <div className="slide-wrapper">
                    <div className="slide-form slide-form-1">
                        {submitted ? (
                            <>
                                <h1 className="login-title">Check your email</h1>
                                <p className="forgot-confirmation">If an account with that email exists, we've sent a password reset link. It expires in 30 minutes.</p>
                                <p className="forgot-confirmation">You can close this tab if you want — or once you've set your new password, log in below.</p>
                                <p className="forgot-confirmation">Didn't get it? You can resend once the timer is up.</p>
                                <button
                                    className="login-button"
                                    onClick={sendResetLink}
                                    disabled={secondsLeft > 0 || submitting}
                                >
                                    {secondsLeft > 0
                                        ? `Resend in ${Math.floor(secondsLeft / 60)}:${String(secondsLeft % 60).padStart(2, "0")}`
                                        : submitting
                                            ? <PulseLoader size={8} color="#2b2e38" />
                                            : "Resend email"}
                                </button>
                            </>
                        ) : (
                            <>
                                <h1 className="login-title">Enter Email</h1>
                                <form onSubmit={handleSubmit} className="login-forgot-form">
                                    <div className="input-group">
                                        <Mail className="input-icon" />
                                        <input
                                            type="email"
                                            placeholder="Email"
                                            value={email}
                                            onChange={(e) => setEmail(e.target.value)}
                                            className={`login-input${emailError ? " login-input--invalid" : ""}`}
                                            required />
                                    </div>
                                    {emailError && <p className="login-field-error">{emailError}</p>}
                                    <button type="submit" className="login-button" disabled={!!validateEmail(email) || submitting}>
                                        {submitting ? <PulseLoader size={8} color="#2b2e38" /> : "Send Reset Link"}
                                    </button>
                                </form>
                            </>
                        )}
                        <div className="forgot-password">
                            <a href="/login" className="forgot-password-link">
                                Back to Login
                            </a>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    )
}
export default ForgotPassword;
