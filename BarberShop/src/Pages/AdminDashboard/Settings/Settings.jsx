import { useState, useEffect, useContext } from "react";
import { Save } from "lucide-react";
import useFetch from "../../../Hooks/useFetch";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import { ToastContext } from "../../../Context/ToastContext";
import LoadingSpinner from "../../../Components/LoadingSpinner/LoadingSpinner";
import ErrorState from "../../../Components/ErrorState/ErrorState";
import { getErrorMessage } from "../../../utils/errorMessage.js";
import "./Settings.css";

const Settings = () => {
    const { data, loading, error, reFetch } = useFetch("/api/Settings", true);
    const [bufferMin, setBufferMin] = useState(0);
    const [defaultAdminDuration, setDefaultAdminDuration] = useState(30);
    const [graceAfterClose, setGraceAfterClose] = useState(0);
    const [saving, setSaving] = useState(false);
    const { showToast } = useContext(ToastContext);

    useEffect(() => {
        if (data) {
            setBufferMin(data.bufferMin ?? 0);
            setDefaultAdminDuration(data.defaultAdminBookingDurationMin ?? 30);
            setGraceAfterClose(data.graceMinutesAfterClose ?? 0);
        }
    }, [data]);

    const handleSave = async () => {
        const value = Number(bufferMin);
        const durationValue = Number(defaultAdminDuration);
        const graceValue = Number(graceAfterClose);
        if (!Number.isInteger(value) || value < 0 || value > 120) {
            showToast("Invalid buffer", "Buffer must be a whole number between 0 and 120 minutes.");
            return;
        }
        if (!Number.isInteger(durationValue) || durationValue < 5 || durationValue > 240) {
            showToast("Invalid duration", "Default admin booking duration must be a whole number between 5 and 240 minutes.");
            return;
        }
        if (!Number.isInteger(graceValue) || graceValue < 0 || graceValue > 120) {
            showToast("Invalid grace period", "Grace after close must be a whole number between 0 and 120 minutes.");
            return;
        }
        try {
            setSaving(true);
            const res = await adminAxios.put("/api/Settings", {
                bufferMin: value,
                defaultAdminBookingDurationMin: durationValue,
                graceMinutesAfterClose: graceValue,
            });
            setBufferMin(res.data.bufferMin);
            setDefaultAdminDuration(res.data.defaultAdminBookingDurationMin);
            setGraceAfterClose(res.data.graceMinutesAfterClose);
            showToast("Settings saved", "Your booking rules have been updated.", "success");
        }
        catch (err) {
            showToast("Couldn't save settings", getErrorMessage(err));
        }
        finally {
            setSaving(false);
        }
    };

    // Live inline validation mirroring UpdateShopSettingsViewModel's [Range] attributes. The fields are
    // pre-filled, so an out-of-range value lights up immediately; Save is blocked until all three pass.
    const rangeErr = (v, min, max, label) => {
        if (v === "" || v == null) return `${label} is required`;
        const n = Number(v);
        if (!Number.isInteger(n) || n < min || n > max)
            return `${label} must be a whole number between ${min} and ${max}`;
        return null;
    };
    const bufferError = rangeErr(bufferMin, 0, 120, "Minutes between bookings");
    const durationError = rangeErr(defaultAdminDuration, 5, 240, "Default admin booking duration");
    const graceError = rangeErr(graceAfterClose, 0, 120, "Grace after close");
    const canSave = !bufferError && !durationError && !graceError;

    if (loading) {
        return <LoadingSpinner message="Loading Settings" color="#e0e0e0" />;
    }

    /* Block the whole form on a failed load. The inputs are seeded from `data`, so falling through to
       the form would show the defaults (0 / 30 / 0) - and a Save from there would overwrite the shop's
       real settings. Better to show nothing editable until the load succeeds. */
    if (error) {
        return (
            <ErrorState
                title="Couldn't load settings"
                message="We couldn't load your shop settings. Saving now could overwrite them, so please retry."
                onRetry={reFetch}
            />
        );
    }

    return (
        <>
            <div className="page-header">
                <div className="page-header-text">
                    <h1 className="page-title">Settings</h1>
                    <p className="page-subtitle">Booking rules for the shop</p>
                </div>
            </div>
            <div className="settings-content-area">
                <div className="settings-card">
                    <div className="form-group">
                        <label className="form-label">Minutes between bookings</label>
                        <p className="settings-hint">
                            Gap required after each booking before the next can start (cleanup / reset
                            time). Set to 0 to allow back-to-back bookings.
                        </p>
                        <input
                            className={`form-input${bufferError ? " form-input--invalid" : ""}`}
                            type="number"
                            min={0}
                            max={120}
                            step={5}
                            value={bufferMin}
                            onChange={(e) => setBufferMin(e.target.value)}
                        />
                        {bufferError && <span className="form-error">{bufferError}</span>}
                    </div>
                    <div className="form-group">
                        <label className="form-label">Default admin booking duration (minutes)</label>
                        <p className="settings-hint">
                            Pre-fills the duration when you create a booking from the admin side (you can
                            still change it per booking). Between 5 and 240 minutes.
                        </p>
                        <input
                            className={`form-input${durationError ? " form-input--invalid" : ""}`}
                            type="number"
                            min={5}
                            max={240}
                            step={5}
                            value={defaultAdminDuration}
                            onChange={(e) => setDefaultAdminDuration(e.target.value)}
                        />
                        {durationError && <span className="form-error">{durationError}</span>}
                    </div>
                    <div className="form-group">
                        <label className="form-label">Grace after close (minutes)</label>
                        <p className="settings-hint">
                            How long a booking may run past closing time. 0 means every appointment must
                            finish by closing; e.g. 15 lets the last client run up to 15 minutes over.
                        </p>
                        <input
                            className={`form-input${graceError ? " form-input--invalid" : ""}`}
                            type="number"
                            min={0}
                            max={120}
                            step={5}
                            value={graceAfterClose}
                            onChange={(e) => setGraceAfterClose(e.target.value)}
                        />
                        {graceError && <span className="form-error">{graceError}</span>}
                    </div>
                    <div className="settings-actions">
                        <button className="btn-primary" onClick={handleSave} disabled={saving || !canSave}>
                            <Save size={16} />
                            {saving ? " Saving..." : " Save"}
                        </button>
                    </div>
                </div>
            </div>
        </>
    );
};

export default Settings;
