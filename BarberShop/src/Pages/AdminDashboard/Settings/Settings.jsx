import { useState, useEffect, useContext } from "react";
import { Save } from "lucide-react";
import useFetch from "../../../Hooks/useFetch";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import { ToastContext } from "../../../Context/ToastContext";
import LoadingSpinner from "../../../Components/LoadingSpinner/LoadingSpinner";
import "./Settings.css";

const Settings = () => {
    const { data, loading } = useFetch("/api/Settings", true);
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
            showToast("Buffer must be a whole number between 0 and 120 minutes");
            return;
        }
        if (!Number.isInteger(durationValue) || durationValue < 5 || durationValue > 240) {
            showToast("Default admin booking duration must be a whole number between 5 and 240 minutes");
            return;
        }
        if (!Number.isInteger(graceValue) || graceValue < 0 || graceValue > 120) {
            showToast("Grace after close must be a whole number between 0 and 120 minutes");
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
            showToast("Settings saved");
        }
        catch (err) {
            showToast(err.response?.data?.message ?? "Failed to save settings");
        }
        finally {
            setSaving(false);
        }
    };

    if (loading) {
        return <LoadingSpinner message="Loading Settings" color="#e0e0e0" />;
    }

    return (
        <>
            <div className="team-header">
                <div>
                    <h2 className="team-title">SETTINGS</h2>
                    <p className="team-subtitle">Booking rules for the shop</p>
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
                            className="form-input"
                            type="number"
                            min={0}
                            max={120}
                            step={5}
                            value={bufferMin}
                            onChange={(e) => setBufferMin(e.target.value)}
                        />
                    </div>
                    <div className="form-group">
                        <label className="form-label">Default admin booking duration (minutes)</label>
                        <p className="settings-hint">
                            Pre-fills the duration when you create a booking from the admin side (you can
                            still change it per booking). Between 5 and 240 minutes.
                        </p>
                        <input
                            className="form-input"
                            type="number"
                            min={5}
                            max={240}
                            step={5}
                            value={defaultAdminDuration}
                            onChange={(e) => setDefaultAdminDuration(e.target.value)}
                        />
                    </div>
                    <div className="form-group">
                        <label className="form-label">Grace after close (minutes)</label>
                        <p className="settings-hint">
                            How long a booking may run past closing time. 0 means every appointment must
                            finish by closing; e.g. 15 lets the last client run up to 15 minutes over.
                        </p>
                        <input
                            className="form-input"
                            type="number"
                            min={0}
                            max={120}
                            step={5}
                            value={graceAfterClose}
                            onChange={(e) => setGraceAfterClose(e.target.value)}
                        />
                    </div>
                    <div className="settings-actions">
                        <button className="btn-primary" onClick={handleSave} disabled={saving}>
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
