import { useState, useContext } from "react";
import "./ForgotPassword.css";
import axios from "axios";
import { Mail, Lock } from "lucide-react";
import { useNavigate } from "react-router-dom";
import { AuthContext } from "../../Context/AuthContext";
import { ToastContext } from "../../Context/ToastContext";
import { ArrowLeft, LockOpen } from "lucide-react";
const ForgotPassword = () => {
    const [email, setEmail] = useState("");
    const [newPassword, setNewPassword] = useState("");
    const [step, setStep] = useState(1);//1 = email, 2 = password
    const [confirmNewPassword, setConfirmNewPassword] = useState("");
    const { setUser } = useContext(AuthContext);
    const navigate = useNavigate();
    const { showToast } = useContext(ToastContext);
    /*const handleSubmit = async(e) =>{
        e.preventDefault();
        try{
            const response = await axios.post("/api/auth/login", {email, password}, {withCredentials:true});
            /* withCredentials:true means the server sends cookies to the browser 
            console.log(response.data);
            setUser(({
                id:response.data.id,
                role:response.data.role
            }))
            if(response.data.role === "ADMIN"){
                navigate("/admin", {replace:true});
            }
            else{
                navigate("/admin/barber/bookings", {replace:true});
            }
        }
        catch(err){
            const message = err.response?.data?.message || "Something went wrong. Please try again";
            //the fallback message is unnecessary here i think since the backend has a response for every error.
            showToast(message);
            console.log(err);
        }
    }*/

    const handleTransition = (e) => {
        e.preventDefault();
        setStep(2);
    }
    const handleSubmit = async(e) =>{
        e.preventDefault();
        try{
            const response = await axios.post("/api/auth/forgot-password", {email, newPassword, confirmNewPassword}, {withCredentials:true});
            setUser({
                id:response.data.id,
                role:response.data.role
            })
            if(response.data.role === "ADMIN"){
            navigate("/admin",{replace:true});
            }
            else{
                navigate("/admin/barber/bookings", {replace:true});
            }
        }
        catch(err){
            const message = err.response?.data?.message || "Something went wrong. Please try again";
            showToast("Failed to set password",message);
            console.log(err);
        }
    }

    return (
        <div className="login-page">
            <div className="forgot-card">
                <div className={`slide-wrapper ${step === 2 ? "step-two" : ""}`}>
                    <div className="slide-form slide-form-1">
                        <h1 className="login-title">Enter Email</h1>
                        <form onSubmit={handleTransition} className="login-forgot-form">
                            <div className="input-group">
                                <Mail className="input-icon" />
                                <input
                                    type="email"
                                    placeholder="Email"
                                    value={email}
                                    onChange={(e) => setEmail(e.target.value)}
                                    className="login-input"
                                    required />
                            </div>
                            <button type="submit" className="login-button">
                                Continue
                            </button>
                        </form>
                        <div className="forgot-password">
                            <a href="/login" className="forgot-password-link">
                                Back to Login
                            </a>
                        </div>
                    </div>
                    <div className="slide-form">
                        <button onClick={() => setStep(1)} className="forgot-back-arrow"><ArrowLeft/></button>
                        <h1 className="login-title">New Password</h1>
                        <form onSubmit = {handleSubmit} className = "login-form">
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
                        <button type = "submit" className = "login-button">
                            Confirm New Password
                        </button>
                        </form>

                    </div>

                </div>
            </div>
        </div>
    )
}
export default ForgotPassword;