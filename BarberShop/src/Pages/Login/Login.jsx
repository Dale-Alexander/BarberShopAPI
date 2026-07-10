import { useState, useContext } from "react";
import "./Login.css";
import axios from "axios";
import { Mail, Lock } from "lucide-react";
import { useNavigate, useLocation } from "react-router-dom";
import { AuthContext } from "../../Context/AuthContext";
import { ToastContext } from "../../Context/ToastContext";
const Login = () => {
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const { setUser } = useContext(AuthContext);
    const navigate = useNavigate();
    const { showToast } = useContext(ToastContext);
    const location = useLocation();
    /* useLocation is used so that if i am not logged in and i visit /admin/team directly, i will get logged out and when i log in
    i will get redirected to /admin/team instead of just /admin. We are basically saving the desired original destination*/
    const handleSubmit = async (e) => {
        e.preventDefault();
        const fromPath = location?.state?.from;

        try {
            const response = await axios.post("/api/auth/login", { email, password }, { withCredentials: true });
            /* withCredentials:true means the server sends cookies to the browser */
            console.log(response.data);
            setUser(({
                id: response.data.id,
                role: response.data.role
            }))
            if (response.data.role === "ADMIN") {
                    navigate(fromPath || "/admin", { replace: true });
            }
            else {
                navigate("/admin/barber/bookings", { replace: true });
            }
        }
        catch (err) {
            const message = err.response?.data?.message || "Something went wrong. Please try again";
            //the fallback message is unnecessary here i think since the backend has a response for every error.
            showToast("Login Failed", message);
            console.log(err);
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
                    <button type="submit" className="login-button">
                        Login
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