import { useState, useContext } from "react";
import { ToastContext } from "../../../Context/ToastContext.jsx";
import "./UserFormModal.css";
import usePhone from "../../../Hooks/usePhone.js";
import FancyPhoneInput from "../../../Components/FancyPhoneInput/FancyPhoneInput.jsx";
const UserFormModal = ({ onConfirm, onCancel }) => {
    const { showToast } = useContext(ToastContext);
    const [name, setName] = useState("");
    const [phone, setPhone] = useState("");
    const { handlePhoneChange, isValid: isPhoneValid } = usePhone(phone);
    const [duration, setDuration] = useState("");
    const handleConfirm = () => {
        if (!isPhoneValid()) {
            showToast("Booking Confirmation Failed", "Please enter a valid phone number");
            return;
        }
        if (!duration) {
            showToast("Booking Confirmation Failed", "Duration is required");
            return;
        }
        onConfirm({ name, phone, duration:Number(duration)});
    }

    const handleCancel = () => {
        setName("");
        setPhone("");
        setDuration("");
        onCancel();
    }
    return (
        <div className="modal-overlay">
            <div className="user-form-modal">
                <h2>Customer Details</h2>
                <p>Fill in the customer's details for this booking.</p>

                <div className="user-form-modal-field">
                    <label>Full Name <span className="user-form-optional">(optional)</span></label>
                    <input
                        type="text"
                        placeholder="e.g. John Doe"
                        value={name}
                        onChange={(e) => setName(e.target.value)}
                    />
                </div>

                <div className="user-form-modal-field">
                    <label>Phone Number <span className="user-form-required">*</span></label>
                    <FancyPhoneInput value={phone} onChange={(val, iso2) => handlePhoneChange(val, iso2, setPhone)}/>
                    {/*error && <span className="user-form-error">{error}</span>*/}
                </div>
                <div className="user-form-modal-field">
                    <label>Duration <span className="user-form-required">*</span></label>
                    <select
                        value={duration}
                        onChange={(e) => setDuration(e.target.value)}
                    >
                        <option value="" disabled>Select duration</option>
                        <option value="15">15 minutes</option>
                        <option value="30">30 minutes</option>
                        <option value="45">45 minutes</option>
                        <option value="60">1 hour</option>
                    </select>
                </div>

                <div className="user-form-modal-actions">
                    <button className="user-form-btn-cancel" onClick={handleCancel}>Cancel</button>
                    <button className="user-form-btn-confirm" onClick={handleConfirm}>Confirm Booking</button>
                </div>
            </div>
        </div>
    )
}
export default UserFormModal;