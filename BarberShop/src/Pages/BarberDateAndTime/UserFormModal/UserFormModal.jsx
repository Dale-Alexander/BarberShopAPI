import { useState } from "react";
import "./UserFormModal.css";
import usePhone from "../../../Hooks/usePhone.js";
import FancyPhoneInput from "../../../Components/FancyPhoneInput/FancyPhoneInput.jsx";
import { validateName, validateNameInline } from "../../../utils/validation.js";
const UserFormModal = ({ onConfirm, onCancel }) => {
    const [name, setName] = useState("");
    const [phone, setPhone] = useState("");
    // Flipped on the first confirm attempt so the full errors (name 2-char minimum, missing phone)
    // show inline per field instead of as a toast.
    const [attempted, setAttempted] = useState(false);
    const { handlePhoneChange, isValid: isPhoneValid } = usePhone(phone);

    // Name is optional, so an empty box never errors. The inline check covers the obvious problems
    // (digits, over-long) as they type; the 2-char minimum is revealed inline only after an attempt,
    // so it doesn't nag mid-typing. The button gate stays on the inline check (min-length never locks it).
    const nameInlineError = name ? validateNameInline(name) : null;
    const nameError = attempted && name ? validateName(name) : nameInlineError;
    const phoneError = (phone || attempted) ? (isPhoneValid() ? null : "Please enter a valid phone number") : null;
    const canConfirm = isPhoneValid() && !nameInlineError;

    const handleConfirm = () => {
        setAttempted(true);
        if (!isPhoneValid()) return;
        // Only when a name was actually entered - blank stays valid (it's optional).
        if (name && validateName(name)) return;
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
                    {/* Same wiring as the checkout fields: purely for assistive tech, no visual change. */}
                    <input
                        type="text"
                        placeholder="e.g. John Doe"
                        value={name}
                        onChange={(e) => setName(e.target.value)}
                        aria-invalid={!!nameError || undefined}
                        aria-describedby={nameError ? "user-form-name-error" : undefined}
                    />
                    {nameError && <span id="user-form-name-error" className="user-form-error">{nameError}</span>}
                </div>

                <div className="user-form-modal-field">
                    <label>Phone Number <span className="user-form-required">*</span></label>
                    <FancyPhoneInput value={phone} onChange={(val, iso2) => handlePhoneChange(val, iso2, setPhone)}
                        invalid={!!phoneError}
                        describedBy={phoneError ? "user-form-phone-error" : undefined}/>
                    {phoneError && <span id="user-form-phone-error" className="user-form-error">{phoneError}</span>}
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