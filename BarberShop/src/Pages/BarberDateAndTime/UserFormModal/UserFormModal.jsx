import { useState, useContext } from "react";
import { ToastContext } from "../../../Context/ToastContext.jsx";
import "./UserFormModal.css";
import usePhone from "../../../Hooks/usePhone.js";
import FancyPhoneInput from "../../../Components/FancyPhoneInput/FancyPhoneInput.jsx";
import { validateName, validateNameInline } from "../../../utils/validation.js";
const UserFormModal = ({ onConfirm, onCancel }) => {
    const { showToast } = useContext(ToastContext);
    const [name, setName] = useState("");
    const [phone, setPhone] = useState("");
    const { handlePhoneChange, isValid: isPhoneValid } = usePhone(phone);

    // Name is optional here, so it's only validated once something's typed. The inline error covers the
    // obvious problems (digits, over-long); a too-short name is left to the toast on confirm. Phone is
    // required and shows its error live once the field has a value.
    const nameError = name ? validateNameInline(name) : null;
    const phoneError = phone && !isPhoneValid() ? "Please enter a valid phone number" : null;
    const canConfirm = isPhoneValid() && !nameError;

    const handleConfirm = () => {
        if (!isPhoneValid()) {
            showToast("Booking Confirmation Failed", "Please enter a valid phone number");
            return;
        }
        // Only when a name was actually entered - blank stays valid (it's optional).
        const fullNameError = name ? validateName(name) : null;
        if (fullNameError) {
            showToast("Booking Confirmation Failed", fullNameError);
            return;
        }
        // Duration is chosen on the date/time page now, so the modal only collects name + phone.
        onConfirm({ name, phone });
    }

    const handleCancel = () => {
        setName("");
        setPhone("");
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
                    {nameError && <span className="user-form-error">{nameError}</span>}
                </div>

                <div className="user-form-modal-field">
                    <label>Phone Number <span className="user-form-required">*</span></label>
                    <FancyPhoneInput value={phone} onChange={(val, iso2) => handlePhoneChange(val, iso2, setPhone)}/>
                    {phoneError && <span className="user-form-error">{phoneError}</span>}
                </div>

                <div className="user-form-modal-actions">
                    <button className="user-form-btn-cancel" onClick={handleCancel}>Cancel</button>
                    <button className="user-form-btn-confirm" onClick={handleConfirm} disabled={!canConfirm}>Confirm Booking</button>
                </div>
            </div>
        </div>
    )
}
export default UserFormModal;