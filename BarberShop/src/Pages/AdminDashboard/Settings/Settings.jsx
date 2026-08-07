import { useState, useEffect, useContext, useRef } from "react";
import { Save } from "lucide-react";
import useFetch from "../../../Hooks/useFetch";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import { ToastContext } from "../../../Context/ToastContext";
import LoadingSpinner from "../../../Components/LoadingSpinner/LoadingSpinner";
import ErrorState from "../../../Components/ErrorState/ErrorState";
import { getErrorMessage } from "../../../utils/errorMessage.js";
import useTimeFieldFlow from "../../../Hooks/useTimeFieldFlow";
import "./Settings.css";

const Settings = () => {
    const { data, loading, error, reFetch } = useFetch("/api/Settings", true);
    const [bufferMin, setBufferMin] = useState(0);
    const [defaultAdminDuration, setDefaultAdminDuration] = useState(30);
    const [graceAfterClose, setGraceAfterClose] = useState(0);
    const [minAdvance, setMinAdvance] = useState(90);
    const [maxAdvance, setMaxAdvance] = useState(60);
    const [refundCutoff, setRefundCutoff] = useState(24);
    const [slotStep, setSlotStep] = useState(30);
    const [saving, setSaving] = useState(false);
    /* Opening hours save separately from the numbers above. Their PUT can be REFUSED - shop hours are a
       ceiling over every barber shift, so narrowing them past someone's rota comes back with the list of
       schedules in the way - and folding that into the same Save would mean one button that half-succeeds
       and a rejection that has to explain which half. */
    const [shopHours, setShopHours] = useState([]);
    const [savingHours, setSavingHours] = useState(false);
    const [hoursConflicts, setHoursConflicts] = useState(null);
    const { showToast } = useContext(ToastContext);

    useEffect(() => {
        if (data) {
            setBufferMin(data.bufferMin ?? 0);
            setDefaultAdminDuration(data.defaultAdminBookingDurationMin ?? 30);
            setGraceAfterClose(data.graceMinutesAfterClose ?? 0);
            setMinAdvance(data.minAdvanceBookingMinutes ?? 90);
            setMaxAdvance(data.maxAdvanceBookingDays ?? 60);
            setRefundCutoff(data.refundCutoffHours ?? 24);
            setSlotStep(data.slotStepMin ?? 30);
            setShopHours(data.shopHours ?? []);
        }
    }, [data]);

    const DAY_NAMES = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /* "17:31" -> "17:31:00", and left alone if it already carries seconds. The API's TimeOnly binding
       needs them; a <input type="time"> never supplies them at minute precision. */
    const withSeconds = (t) => (t && t.split(":").length === 2 ? `${t}:00` : t);

    // Popup-closing and next-field navigation for the seven pairs of time inputs below.
    const hoursGridRef = useRef(null);
    const { fieldProps: timeFieldProps, onValueChange: onTimeChange } = useTimeFieldFlow(hoursGridRef);

    const setDay = (dayOfWeek, patch) =>
        setShopHours((prev) => prev.map((d) => (d.dayOfWeek === dayOfWeek ? { ...d, ...patch } : d)));

    /* An open day whose times are back to front has no bookable minute in it, and the server rejects it -
       so catch it here rather than after a round trip. Closed days are exempt: their times are ignored and
       kept only so reopening the day restores what was there before. */
    const hoursError = shopHours.some((d) => !d.isClosed && d.openTime >= d.closeTime)
        ? "Opening time must be before closing time."
        : null;

    const handleSaveHours = async () => {
        try {
            setSavingHours(true);
            setHoursConflicts(null);
            const res = await adminAxios.put("/api/Settings/hours", {
                days: shopHours.map((d) => ({
                    dayOfWeek: d.dayOfWeek,
                    /* Seconds appended because the API binds these to TimeOnly, which rejects "17:31"
                       outright - the request never reaches the controller and comes back as a bare model
                       binding 400, which the page could only report as "something went wrong". A time
                       input yields "HH:mm", so the seconds have to be added here. Schedules.jsx does the
                       same thing to the same API for the same reason. */
                    openTime: withSeconds(d.openTime),
                    closeTime: withSeconds(d.closeTime),
                    isClosed: d.isClosed,
                })),
            });
            setShopHours(res.data);
            showToast("Opening hours saved", "The shop's opening hours have been updated.", "success");
        }
        catch (err) {
            /* The refusal that names which barber schedules stick out past the new hours. Rendered as a
               list under the section rather than a toast: it's a to-do (go fix these schedules first), and
               a toast would vanish before the admin could act on it. */
            const conflicts = err.response?.data?.conflicts;
            if (conflicts?.length) setHoursConflicts({ message: err.response.data.message, conflicts });
            else showToast("Couldn't save opening hours", getErrorMessage(err));
        }
        finally {
            setSavingHours(false);
        }
    };

    const handleSave = async () => {
        // Save is gated on canSave (all three inline range validators pass), so the values are already
        // valid by the time we get here - just coerce to numbers for the payload. The out-of-range
        // feedback lives inline under each field, not in a toast.
        const value = Number(bufferMin);
        const durationValue = Number(defaultAdminDuration);
        const graceValue = Number(graceAfterClose);
        const minAdvanceValue = Number(minAdvance);
        const maxAdvanceValue = Number(maxAdvance);
        const refundCutoffValue = Number(refundCutoff);
        try {
            setSaving(true);
            const res = await adminAxios.put("/api/Settings", {
                bufferMin: value,
                defaultAdminBookingDurationMin: durationValue,
                graceMinutesAfterClose: graceValue,
                minAdvanceBookingMinutes: minAdvanceValue,
                maxAdvanceBookingDays: maxAdvanceValue,
                refundCutoffHours: refundCutoffValue,
                slotStepMin: Number(slotStep),
            });
            setBufferMin(res.data.bufferMin);
            setDefaultAdminDuration(res.data.defaultAdminBookingDurationMin);
            setGraceAfterClose(res.data.graceMinutesAfterClose);
            setMinAdvance(res.data.minAdvanceBookingMinutes);
            setMaxAdvance(res.data.maxAdvanceBookingDays);
            setRefundCutoff(res.data.refundCutoffHours);
            setSlotStep(res.data.slotStepMin);
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
    const minAdvanceError = rangeErr(minAdvance, 0, 1440, "Minimum advance booking");
    const maxAdvanceError = rangeErr(maxAdvance, 1, 365, "Maximum advance booking");
    const refundCutoffError = rangeErr(refundCutoff, 0, 168, "Refund cutoff");
    const canSave = !bufferError && !durationError && !graceError
        && !minAdvanceError && !maxAdvanceError && !refundCutoffError;

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
            {/* Grouped into sections by what each rule governs, so the page reads as three decisions
                rather than six unrelated numbers. Each section is a full-width card; the fields inside
                flow in a responsive grid (see .settings-grid). */}
            <div className="settings-content-area">
                <section className="settings-section">
                    <div className="settings-section-head">
                        <h2 className="settings-section-title">Appointment timing</h2>
                        <p className="settings-section-desc">
                            How long appointments run, and the gaps the shop needs around them.
                        </p>
                    </div>
                    <div className="settings-grid">
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
                        <div className="form-group">
                            <label className="form-label">Time between slots (minutes)</label>
                            <p className="settings-hint">
                                How far apart the times offered in the booking picker are. 30 shows 09:00,
                                09:30, 10:00; 15 also shows 09:15 and 09:45. Shorter steps give customers
                                more choice but can leave small unfillable gaps between appointments.
                            </p>
                            {/* A select, not a number box like its neighbours: the allowed values are a set,
                                not a range. The step has to divide 60 or the times walk off the hour (25
                                would give 09:00, 09:25, 09:50, 10:15), and there's no min/max that says so. */}
                            <select
                                className="form-input"
                                value={slotStep}
                                onChange={(e) => setSlotStep(e.target.value)}
                            >
                                {[5, 10, 15, 20, 30].map((n) => (
                                    <option key={n} value={n}>{n} minutes</option>
                                ))}
                            </select>
                        </div>
                    </div>
                </section>

                <section className="settings-section">
                    <div className="settings-section-head">
                        <h2 className="settings-section-title">Opening hours</h2>
                        <p className="settings-section-desc">
                            When the shop is open, per day. Barbers' working hours must fit inside these, so
                            you'll need to adjust their schedules before shortening a day past their shifts.
                        </p>
                    </div>
                    <div className="settings-hours" ref={hoursGridRef}>
                        {shopHours.map((d) => (
                            <div className="settings-hours-row" key={d.dayOfWeek}>
                                <span className="settings-hours-day">{DAY_NAMES[d.dayOfWeek]}</span>
                                <label className="settings-hours-closed">
                                    <input
                                        type="checkbox"
                                        checked={d.isClosed}
                                        onChange={(e) => setDay(d.dayOfWeek, { isClosed: e.target.checked })}
                                    />
                                    <span>Closed</span>
                                </label>
                                {/* Left in place but disabled while closed, rather than hidden: the times are
                                    kept server-side too, so reopening a day brings back the hours it used to
                                    have instead of an empty form. */}
                                <input
                                    className="form-input settings-hours-time"
                                    type="time"
                                    value={d.openTime}
                                    disabled={d.isClosed}
                                    {...timeFieldProps}
                                    onChange={(e) => { setDay(d.dayOfWeek, { openTime: e.target.value }); onTimeChange(e); }}
                                />
                                <span className="settings-hours-sep">to</span>
                                <input
                                    className="form-input settings-hours-time"
                                    type="time"
                                    value={d.closeTime}
                                    disabled={d.isClosed}
                                    {...timeFieldProps}
                                    onChange={(e) => { setDay(d.dayOfWeek, { closeTime: e.target.value }); onTimeChange(e); }}
                                />
                            </div>
                        ))}
                    </div>
                    {hoursError && <span className="form-error">{hoursError}</span>}
                    {hoursConflicts && (
                        <div className="settings-hours-conflicts">
                            <p>{hoursConflicts.message}</p>
                            <ul>
                                {hoursConflicts.conflicts.map((c, i) => (
                                    <li key={i}>
                                        <strong>{c.barberName || `Barber ${c.barberId}`}</strong> — {c.day} {c.shift}: {c.reason}
                                    </li>
                                ))}
                            </ul>
                        </div>
                    )}
                    <div className="settings-actions">
                        <button
                            className="btn-primary"
                            onClick={handleSaveHours}
                            disabled={savingHours || !!hoursError || shopHours.length === 0}
                        >
                            <Save size={16} />
                            {savingHours ? " Saving..." : " Save opening hours"}
                        </button>
                    </div>
                </section>

                <section className="settings-section">
                    <div className="settings-section-head">
                        <h2 className="settings-section-title">Customer booking window</h2>
                        <p className="settings-section-desc">
                            How near and how far ahead customers can book. Staff bookings ignore both.
                        </p>
                    </div>
                    <div className="settings-grid">
                        <div className="form-group">
                            <label className="form-label">Minimum advance booking (minutes)</label>
                            <p className="settings-hint">
                                How far ahead a customer must book. 0 lets them book right up to the slot time;
                                e.g. 90 means the next 90 minutes are closed to new bookings. Between 0 and 1440
                                (24 hours). Staff bookings aren't affected.
                            </p>
                            <input
                                className={`form-input${minAdvanceError ? " form-input--invalid" : ""}`}
                                type="number"
                                min={0}
                                max={1440}
                                step={15}
                                value={minAdvance}
                                onChange={(e) => setMinAdvance(e.target.value)}
                            />
                            {minAdvanceError && <span className="form-error">{minAdvanceError}</span>}
                        </div>
                        <div className="form-group">
                            <label className="form-label">Maximum advance booking (days)</label>
                            <p className="settings-hint">
                                How far into the future a customer can book. Between 1 and 365 days. Staff
                                bookings aren't affected.
                            </p>
                            <input
                                className={`form-input${maxAdvanceError ? " form-input--invalid" : ""}`}
                                type="number"
                                min={1}
                                max={365}
                                step={1}
                                value={maxAdvance}
                                onChange={(e) => setMaxAdvance(e.target.value)}
                            />
                            {maxAdvanceError && <span className="form-error">{maxAdvanceError}</span>}
                        </div>
                    </div>
                </section>

                <section className="settings-section">
                    <div className="settings-section-head">
                        <h2 className="settings-section-title">Cancellations &amp; refunds</h2>
                        <p className="settings-section-desc">
                            When a customer cancelling late stops getting their money back.
                        </p>
                    </div>
                    <div className="settings-grid">
                        <div className="form-group">
                            <label className="form-label">Refund cutoff (hours)</label>
                            <p className="settings-hint">
                                Cancelling within this many hours of the appointment forfeits the customer's
                                refund (too little time to rebook). 0 always refunds. Between 0 and 168 (one
                                week). Shop-side and staff-forced cancellations always refund regardless.
                            </p>
                            <input
                                className={`form-input${refundCutoffError ? " form-input--invalid" : ""}`}
                                type="number"
                                min={0}
                                max={168}
                                step={1}
                                value={refundCutoff}
                                onChange={(e) => setRefundCutoff(e.target.value)}
                            />
                            {refundCutoffError && <span className="form-error">{refundCutoffError}</span>}
                        </div>
                    </div>
                </section>

                <div className="settings-actions">
                    <button className="btn-primary" onClick={handleSave} disabled={saving || !canSave}>
                        <Save size={16} />
                        {saving ? " Saving..." : " Save"}
                    </button>
                </div>
            </div>
        </>
    );
};

export default Settings;
