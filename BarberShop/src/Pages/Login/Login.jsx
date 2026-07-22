import { useState, useContext } from "react";
import "./Login.css";
import axios from "axios";
import { PulseLoader } from "react-spinners";
import { Mail, Lock } from "lucide-react";
import { useNavigate, useLocation } from "react-router-dom";
import { AuthContext } from "../../Context/AuthContext";
import { ToastContext } from "../../Context/ToastContext";
import { getErrorMessage } from "../../utils/errorMessage.js";
const Login = () => {
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const { setUser } = useContext(AuthContext);
    const navigate = useNavigate();
    const { showToast } = useContext(ToastContext);
    const location = useLocation();
    const [submitting, setSubmitting] = useState(false);
    /* useLocation is used so that if i am not logged in and i visit /admin/team directly, i will get logged out and when i log in
    i will get redirected to /admin/team instead of just /admin. We are basically saving the desired original destination*/
    const handleSubmit = async (e) => {
        e.preventDefault();
        const fromPath = location?.state?.from;
        if (submitting) return; // guard against a double-submit while the request is in flight
        setSubmitting(true);

        try {
            const response = await axios.post("/api/auth/login", { email, password }, { withCredentials: true });
            /* withCredentials:true means the server sends cookies to the browser */
            console.log(response.data);
            setUser(({
                id: response.data.id,
                role: response.data.role,
                barberId: response.data.barberId
            }))
            if (response.data.role === "ADMIN") {
                    navigate(fromPath || "/admin", { replace: true });
            }
            else {
                // A barber's own bookings live at /admin/team/{barberId} (BarberBookings).
                navigate(`/admin/team/${response.data.barberId}`, { replace: true });
            }
        }
        catch (err) {
            // Clear the password on any failed attempt (keep the email so a typo is easy to fix).
            setPassword("");
            const status = err.response?.status;
            // The backend now returns a single generic 401 for every credential failure, so the UI
            // matches it: no hint about whether the email exists or which field was wrong. Anything
            // else (network down, server error) still gets getErrorMessage's specific/offline text.
            if (status === 401 || status === 404) {
                showToast("Login Failed", "Invalid email or password.");
            } else {
                showToast("Login Failed", getErrorMessage(err));
            }
            console.log(err);
            // Only reset submitting on failure - a success navigates away and unmounts this component.
            setSubmitting(false);
        }
    }

    return (
        <div className="login-page">
            <div className="login-card">
                <h1 className="login-title">Log In</h1>
                <form onSubmit={handleSubmit} className="login-form">
                    <div className="input-group">
                        <Mail className="input-icon" />
                        <input
                            type="email"
                            placeholder="Email"
                            value={email}
                            onChange={(e) =>
                                setEmail(e.target.value)}
                            className="login-input"
                            required />
                    </div>
                    <div className="input-group">
                        <Lock className="input-icon" />
                        <input type="password"
                            placeholder="Password"
                            value={password}
                            onChange={(e) => setPassword(e.target.value)}
                            className="login-input"
                            required />
                    </div>
                    <button type="submit" className="login-button" disabled={submitting}>
                        {submitting ? <PulseLoader size={8} color="#2b2e38" /> : "Login"}
                    </button>
                </form>
                <div className="forgot-password">
                    <a href="/forgot-password" className="forgot-password-link">
                        Forgot your password?
                    </a>
                </div>
            </div>
        </div>
    )
}
export default Login;