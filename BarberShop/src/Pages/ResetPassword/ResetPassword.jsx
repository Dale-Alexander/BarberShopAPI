import { useState } from "react";
import "../ForgotPassword/ForgotPassword.css";
import axios from "axios";
import { Lock, LockOpen } from "lucide-react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { ToastContext } from "../../Context/ToastContext";
import { useContext } from "react";

// Step 2 of the password reset flow, reached only via the link emailed by
// RequestPasswordReset (authController.cs) - the token in the URL is what proves this
// visitor actually owns the account, there's no login/session involved anywhere here.
const ResetPassword = () => {
    const [searchParams] = useSearchParams();
    const token = searchParams.get("token"); // raw token from the emailed link, not the hash stored server-side
    const [newPassword, setNewPassword] = useState("");
    const [confirmNewPassword, setConfirmNewPassword] = useState("");
    const navigate = useNavigate();
    const { showToast } = useContext(ToastContext);

    const handleSubmit = async (e) => {
        e.preventDefault();
        try {
            await axios.post("/api/auth/reset-password", { token, newPassword, confirmNewPassword });
            // No cookie/session is set by this request on purpose (see ResetPassword in
            // authController.cs) - send them to log in fresh with the new password
            // instead of trusting this request alone to establish a session.
            showToast("Password updated", "Please log in with your new password");
            navigate("/login", { replace: true });
        }
        catch (err) {
            // Covers both "token expired/already used" and validation errors
            // (e.g. passwords don't match) - the backend message is specific enough
            // to show directly.
            const message = err.response?.data?.message || "Something went wrong. Please try again";
            showToast("Failed to reset password", message);
        }
    }

    if (!token) {
        return (
            <div className="login-page">
                <div className="forgot-card">
                    <div className="slide-wrapper">
                        <div className="slide-form slide-form-1">
                            <h1 className="login-title">Invalid Link</h1>
                            <p>This password reset link is missing or invalid.</p>
                            <div className="forgot-password">
                                <a href="/forgot-password" className="forgot-password-link">
                                    Request a new link
                                </a>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        )
    }

    return (
        <div className="login-page">
            <div className="forgot-card">
                <div className="slide-wrapper">
                    <div className="slide-form slide-form-1">
                        <h1 className="login-title">New Password</h1>
                        <form onSubmit={handleSubmit} className="login-form">
                            <div className="input-group">
                                <Lock className="input-icon" />
                                <input type="password"
                                    placeholder="Password"
                                    value={newPassword}
                                    onChange={(e) => setNewPassword(e.target.value)}
                                    className="login-input"
                                    required />
                            </div>
                            <div className="input-group">
                                <LockOpen className="input-icon" />
                                <input type="password"
                                    placeholder="Confirm Password"
                                    value={confirmNewPassword}
                                    onChange={(e) => setConfirmNewPassword(e.target.value)}
                                    className="login-input"
                                    required />
                            </div>
                            <button type="submit" className="login-button">
                                Confirm New Password
                            </button>
                        </form>
                    </div>
                </div>
            </div>
        </div>
    )
}
export default ResetPassword;
