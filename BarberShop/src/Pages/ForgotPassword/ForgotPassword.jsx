import { useState } from "react";
import "./ForgotPassword.css";
import axios from "axios";
import { PulseLoader } from "react-spinners";
import { Mail } from "lucide-react";
import { validateEmail } from "../../utils/validation";

// Step 1 of the password reset flow: just collects an email and asks the backend to
// send a reset link. This page never sees or sets a new password - that only happens
// on ResetPassword.jsx, reached via the link in the email that RequestPasswordReset
// (authController.cs) enqueues.
const ForgotPassword = () => {
    const [email, setEmail] = useState("");
    const [submitted, setSubmitted] = useState(false);
    const [submitting, setSubmitting] = useState(false);

    // Shown live once the user starts typing; the button stays locked until the address is well-formed.
    const emailError = email ? validateEmail(email) : null;

    const handleSubmit = async (e) => {
        e.preventDefault();
        if (validateEmail(email)) return; // safety net; the button is disabled until the email is valid
        if (submitting) return;
        setSubmitting(true);
        try {
            await axios.post("/api/auth/forgot-password", { email });
        }
        finally {
            // Always show the same confirmation regardless of outcome - the backend
            // intentionally never reveals whether the email exists, so the UI
            // shouldn't either (even a failed request looks identical to a success).
            setSubmitting(false);
            setSubmitted(true);
        }
    }

    return (
        <div className="login-page">
            <div className="forgot-card">
                <div className="slide-wrapper">
                    <div className="slide-form slide-form-1">
                        {submitted ? (
                            <>
                                <h1 className="login-title">Check your email</h1>
                                <p className="forgot-confirmation">If an account with that email exists, we've sent a password reset link. It expires in 30 minutes.</p>
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
