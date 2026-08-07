import { useState, useEffect, useContext, useCallback, useRef } from "react";
import { useSearchParams } from "react-router-dom";
import { Plus, Trash2, Save, CalendarPlus } from "lucide-react";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import useFetch from "../../../Hooks/useFetch";
import { ToastContext } from "../../../Context/ToastContext";
import { AuthContext } from "../../../Context/AuthContext";
import LoadingSpinner from "../../../Components/LoadingSpinner/LoadingSpinner";
import ErrorState from "../../../Components/ErrorState/ErrorState";
import { getErrorMessage } from "../../../utils/errorMessage.js";
import { formatPhone } from "../../../utils/phone.js";
import useTimeFieldFlow from "../../../Hooks/useTimeFieldFlow";
import "./Schedules.css";

const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const todayStr = () => {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};
const fmtDate = (iso) => new Date(iso + "T00:00:00").toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
// Local-midnight arithmetic, matching todayStr's format. Date handles month/year rollover for us.
const addDays = (iso, n) => {
    const d = new Date(iso + "T00:00:00");
    d.setDate(d.getDate() + n);
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};

// version.shifts (flat [{dayOfWeek,startTime,endTime}]) -> 7 arrays of {start,end} "HH:mm" ranges.
const versionToDays = (version) => {
    const days = Array.from({ length: 7 }, () => []);
    (version?.shifts ?? []).forEach((s) => {
        days[s.dayOfWeek].push({ start: s.startTime.slice(0, 5), end: s.endTime.slice(0, 5) });
    });
    days.forEach((d) => d.sort((a, b) => a.start.localeCompare(b.start)));
    return days;
};
// 7 day arrays -> flat shift payload; TimeOnly binds cleanly from "HH:mm:ss".
const daysToShifts = (days) =>
    days.flatMap((ranges, dow) => ranges.map((r) => ({ dayOfWeek: dow, startTime: `${r.start}:00`, endTime: `${r.end}:00` })));

// How a version relates to today, for labelling and edit-locking.
const versionState = (v) => {
    const t = todayStr();
    if (v.effectiveTo == null) return v.effectiveFrom > t ? "upcoming" : "active";
    if (v.effectiveTo < t) return "ended";
    return v.effectiveFrom > t ? "upcoming" : "active";
};
/* Every label leads with "Hours" on purpose. These chips sit right under a barber's name, and the old
   "Active now" read as a statement about the BARBER (as in, currently employed / on shift) rather than
   about which set of working hours is in force. Naming the thing removes the ambiguity, and keeping the
   three labels parallel makes the row scan as one timeline. */
const versionLabel = (v) => {
    const s = versionState(v);
    if (s === "active") return "Schedule in effect now";
    if (s === "upcoming") return `Schedule from ${fmtDate(v.effectiveFrom)}`;
    return `Schedule until ${fmtDate(v.effectiveTo)}`;
};

const Schedules = () => {
    const { user } = useContext(AuthContext);
    /* Barbers reach this page to READ the shifts they've been given - setting them is the admin's. The
     * flag turns off every control below (see isReadOnly) and, just as importantly, keeps them off
     * /api/Barbers/admin: that roster is ADMIN-only, so fetching it as a barber would 403 and strand the
     * page on its error state before it ever rendered their hours. They don't need it either - there's no
     * barber to pick when the only schedule they may read is their own. */
    const isBarber = user?.role === "BARBER";
    const { data: barbersData, loading: barbersLoading, error: barbersError } =
        useFetch(isBarber ? null : "/api/Barbers/admin", true);
    /* Shop hours bound every shift, so the editor shows that bound instead of letting the admin type a
     * time the save will refuse. /api/Settings is staff-readable, so a barber viewing their own hours
     * gets it too. Not a substitute for the server check - the backend still names the offending shift,
     * and a stale page could post anything - just a way to fail before the round trip. */
    const { data: shopSettings } = useFetch("/api/Settings", true);
    const shopHours = shopSettings?.shopHours ?? [];
    const hoursForDay = (dow) => shopHours.find((h) => h.dayOfWeek === dow) ?? null;

    /* Any minute, deliberately - a shift may end at 17:10 even where slots are offered every 15.
     *
     * The two are different things. A shift says when the barber is HERE; the slot step says which start
     * times the picker OFFERS. Nothing on the server ties them: ValidateShifts only checks start<end and
     * no overlap, and the shop-hours rule only checks the outer bound. Tying this input to slotStepMin
     * made the editor stricter than the thing it edits - a rule the UI invented - and it bit immediately:
     * a 15-minute step made 17:10 unreachable for no reason anyone could have looked up.
     *
     * A trailing part-slot is simply unbookable, which is the shop's business, not the editor's: at a
     * 15-minute step a 17:10 finish just means nothing starts after 17:00 (or later, with grace). */
    const shiftStepSeconds = 60;
    const { showToast } = useContext(ToastContext);
    const [searchParams, setSearchParams] = useSearchParams();

    const [barberId, setBarberId] = useState(null);
    const [versions, setVersions] = useState(null); // null = not loaded for the current barber yet
    const [versionsLoading, setVersionsLoading] = useState(false);
    const [selectedVersionId, setSelectedVersionId] = useState(null);
    const [days, setDays] = useState(Array.from({ length: 7 }, () => []));
    const [saving, setSaving] = useState(false);
    const [newFrom, setNewFrom] = useState(todayStr());
    const [creating, setCreating] = useState(false);
    // Set when a save/create would strand confirmed future bookings: { kind, message, affected[] }.
    const [orphanConflict, setOrphanConflict] = useState(null);
    /* The other direction: bookings that were flagged for falling outside the hours and now fit again,
       listed after a successful save so the admin knows to go and clear them. */
    const [backInside, setBackInside] = useState(null);

    const activeBarbers = (barbersData ?? []).filter((b) => b.isActive);
    const selectedVersion = (versions ?? []).find((v) => v.id === selectedVersionId) ?? null;
    /* Two reasons a version can't be edited, and they carry different messages: it has already ended (true
     * for the admin too - past hours are history), or the viewer is a barber, for whom every version is
     * read-only. One flag drives the whole grid: disabled time inputs, and the hidden Add shift / Remove
     * shift / Save / Remove-schedule controls. */
    const isReadOnly = isBarber || (selectedVersion ? versionState(selectedVersion) === "ended" : true);
    const canDelete = !isBarber && selectedVersion && selectedVersion.effectiveTo == null && (versions ?? []).length > 1;

    /* Earliest date a new seasonal change may start, mirroring CreateVersion's two guards exactly:
     *   - nothing may start in the past (EffectiveFrom < ShopClock.Today), and
     *   - it must start AFTER the open-ended version began (EffectiveFrom <= current.EffectiveFrom).
     * The open-ended version (effectiveTo == null) is the one the backend calls `current`, and chaining
     * guarantees it's the one with the latest effectiveFrom - so when a change is already scheduled for
     * September, that September date is the floor, not today's. The picker used to allow anything from
     * today, which let the admin choose a date the backend would then reject.
     *
     * Derived, not clamped into state: newFromValue is what the field shows and what gets posted, so an
     * already-typed date that's too early corrects itself instead of sitting there waiting to 400. */
    const openEndedVersion = (versions ?? []).find((v) => v.effectiveTo == null) ?? null;
    const dayAfterLatest = openEndedVersion ? addDays(openEndedVersion.effectiveFrom, 1) : null;
    const earliestNewFrom = dayAfterLatest && dayAfterLatest > todayStr() ? dayAfterLatest : todayStr();
    const newFromValue = newFrom < earliestNewFrom ? earliestNewFrom : newFrom;

    const loadVersions = useCallback(async (id, preferVersionId = null) => {
        setVersionsLoading(true);
        try {
            const res = await adminAxios.get(`/api/Schedules/barber/${id}`);
            setVersions(res.data);
            // Prefer an explicit version (e.g. just-created), else the one active today, else the last.
            const active = res.data.find((v) => versionState(v) === "active");
            const pick = preferVersionId ?? active?.id ?? res.data[res.data.length - 1]?.id ?? null;
            setSelectedVersionId(pick);
        } catch (err) {
            showToast("Couldn't load schedule", getErrorMessage(err));
            setVersions([]);
        } finally {
            setVersionsLoading(false);
        }
    }, [showToast]);

    /* Widening a barber's hours saves without stopping to ask anything (only narrowing does, because only
       narrowing strands bookings), so an admin undoing a mistake gets no hint that the bookings it flagged
       now fit again - and those notes still say the booking falls outside the schedule. This lists them.

       Deliberately a list to go and check, not a "clear them all" button. A flagged booking's note can
       carry other problems as well - a refund that failed, a customer nobody has phoned - and widening the
       hours does nothing about those, so each one is read and cleared by hand. */
    const announceBackInsideHours = (bookings) => {
        if (bookings?.length) setBackInside(bookings);
    };

    /* Preselect a barber from ?barberId (the create-barber handoff) once the roster is in. A barber has no
       roster and no choice to make - they're pinned to their own id, and the ?barberId param is ignored
       rather than honoured, so editing it in the URL can't even attempt someone else's schedule (the
       backend 403s that too). */
    useEffect(() => {
        if (barberId != null) return;
        if (isBarber) {
            if (user?.barberId != null) setBarberId(user.barberId);
            return;
        }
        if (activeBarbers.length === 0) return;
        const fromQuery = Number(searchParams.get("barberId"));
        const initial = activeBarbers.some((b) => b.id === fromQuery) ? fromQuery : activeBarbers[0].id;
        setBarberId(initial);
    }, [activeBarbers, barberId, searchParams, isBarber, user?.barberId]);

    useEffect(() => {
        if (barberId != null) loadVersions(barberId);
    }, [barberId, loadVersions]);

    // Seed the day editor whenever the selected version changes.
    useEffect(() => {
        setDays(versionToDays(selectedVersion));
    }, [selectedVersionId, versions]); // eslint-disable-line react-hooks/exhaustive-deps

    const onPickBarber = (id) => {
        /* Picking the barber already shown is not just wasted work, it used to break the page. The two
           clears below empty the editor immediately, but refilling it is the barberId effect's job -
           and setBarberId to the value it already holds is a no-op, so that effect never re-ran. The
           result was a blank week with no spinner and no error, until the admin reloaded. The first
           barber in the roster is preselected on load, so choosing them in the dropdown was enough. */
        if (id === barberId) return;
        setBarberId(id);
        setVersions(null);
        setSelectedVersionId(null);
        setSearchParams((prev) => { prev.set("barberId", String(id)); return prev; }, { replace: true });
    };

    /* A new shift starts as the shop's full opening hours for that weekday rather than a hardcoded
     * 09:00-17:30, which the save would refuse outright on a shop that opens at 10. Falls back to the old
     * pair only before settings load. */
    const addShift = (dow) => {
        const hours = hoursForDay(dow);
        const start = hours && !hours.isClosed ? hours.openTime : "09:00";
        const end = hours && !hours.isClosed ? hours.closeTime : "17:30";
        setDays((prev) => prev.map((r, i) => (i === dow ? [...r, { start, end }] : r)));
    };
    const removeShift = (dow, idx) => {
        setDays((prev) => prev.map((r, i) => (i === dow ? r.filter((_, j) => j !== idx) : r)));
    };
    const setShift = (dow, idx, field, value) => {
        setDays((prev) => prev.map((r, i) => (i === dow ? r.map((s, j) => (j === idx ? { ...s, [field]: value } : s)) : r)));
    };

    // Popup-closing and next-field navigation for the week grid: start -> end -> next day.
    const weekRef = useRef(null);
    const { fieldProps: timeFieldProps, onValueChange: onTimeChange } = useTimeFieldFlow(weekRef);


    // Client mirror of the backend's ValidateShifts, so a bad grid is caught before the request.
    const shiftError = (() => {
        for (let dow = 0; dow < 7; dow++) {
            const ranges = [...days[dow]].sort((a, b) => a.start.localeCompare(b.start));
            for (let i = 0; i < ranges.length; i++) {
                if (!ranges[i].start || !ranges[i].end || ranges[i].start >= ranges[i].end)
                    return `${DAYS[dow]}: each shift must start before it ends`;
                if (i > 0 && ranges[i].start < ranges[i - 1].end)
                    return `${DAYS[dow]}: shifts can't overlap`;
            }
        }
        return null;
    })();

    const handleSave = async (confirmOrphaned = false) => {
        if (shiftError || isReadOnly || !selectedVersion) return;
        setSaving(true);
        try {
            const res = await adminAxios.put(`/api/Schedules/version/${selectedVersion.id}`,
                { shifts: daysToShifts(days), confirmOrphaned });
            setOrphanConflict(null);
            showToast("Schedule saved", "The barber's working hours have been updated.", "success");
            announceBackInsideHours(res.data?.backInsideHours);
            await loadVersions(barberId, selectedVersion.id);
        } catch (err) {
            // Confirmed future bookings would fall outside the new hours - review them, then re-save.
            if (err.response?.status === 409 && err.response.data?.requiresConfirmation) {
                setOrphanConflict({ kind: "save", ...err.response.data });
            } else {
                showToast("Couldn't save schedule", getErrorMessage(err));
            }
        } finally {
            setSaving(false);
        }
    };

    const handleCreateVersion = async (confirmOrphaned = false) => {
        // isBarber, like the isReadOnly/canDelete guards on save and delete: the button is hidden either
        // way, this just keeps all three write paths refusing on the same terms.
        if (creating || isBarber) return;
        setCreating(true);
        try {
            // Seed the new season from whatever's currently in the editor, so the admin refines from the
            // existing hours rather than a blank week.
            const res = await adminAxios.post(`/api/Schedules/barber/${barberId}`, {
                effectiveFrom: newFromValue,
                shifts: daysToShifts(days),
                confirmOrphaned,
            });
            setOrphanConflict(null);
            showToast(
                "Schedule change created",
                `New hours take effect from ${fmtDate(newFromValue)}. The previous schedule ends the day before.`,
                "success");
            announceBackInsideHours(res.data?.backInsideHours);
            await loadVersions(barberId, res.data.id);
        } catch (err) {
            if (err.response?.status === 409 && err.response.data?.requiresConfirmation) {
                setOrphanConflict({ kind: "create", ...err.response.data });
            } else {
                showToast("Couldn't create schedule change", getErrorMessage(err));
            }
        } finally {
            setCreating(false);
        }
    };

    const handleDelete = async (confirmOrphaned = false) => {
        if (!canDelete) return;
        setSaving(true);
        try {
            const res = await adminAxios.delete(
                `/api/Schedules/version/${selectedVersion.id}?confirmOrphaned=${confirmOrphaned}`);
            setOrphanConflict(null);
            showToast("Schedule removed", "Reverted to the previous schedule.", "success");
            announceBackInsideHours(res.data?.backInsideHours);
            await loadVersions(barberId);
        } catch (err) {
            // Restoring the previous hours would strand confirmed bookings - same review-then-confirm flow
            // as saving and creating, so the admin isn't told about them only after the fact.
            if (err.response?.status === 409 && err.response.data?.requiresConfirmation) {
                setOrphanConflict({ kind: "delete", ...err.response.data });
            } else {
                showToast("Couldn't remove schedule", getErrorMessage(err));
            }
        } finally {
            setSaving(false);
        }
    };

    // Roster states only apply to the admin - a barber never requests it, so these would otherwise be
    // permanently falsy for them and are gated for clarity rather than necessity.
    if (!isBarber && barbersLoading) return <LoadingSpinner message="Loading schedules" color="#e0e0e0" />;
    if (!isBarber && barbersError) return <ErrorState title="Couldn't load barbers" message="Please retry." />;

    return (
        <>
            <div className="page-header">
                <div className="page-header-text">
                    <h1 className="page-title">{isBarber ? "My Schedule" : "Schedules"}</h1>
                    <p className="page-subtitle">
                        {isBarber
                            ? "The hours and shifts you're rostered for. Your manager sets these — ask them if something needs changing."
                            /* Mentions the ceiling, because it is now the commonest way a save is refused
                               and the rule is invisible until it bites - the fields cap at the shop's
                               hours and a closed day offers no Add shift at all. */
                            : "Set each barber's working hours, split shifts and seasonal changes. Shifts must fit inside the shop's opening hours (Settings)."}
                    </p>
                </div>
            </div>

            <div className="sched-content">
                <div className="sched-toolbar">
                    {/* Nothing to pick when the only schedule you may read is your own. */}
                    {!isBarber && (
                        <label className="sched-field">
                            <span>Barber</span>
                            <select value={barberId ?? ""} onChange={(e) => onPickBarber(Number(e.target.value))}>
                                {activeBarbers.map((b) => (
                                    <option key={b.id} value={b.id}>{b.firstName} {b.lastName}</option>
                                ))}
                            </select>
                        </label>
                    )}

                    {versions && versions.length > 0 && (
                        <div className="sched-versions">
                            {versions.map((v) => (
                                <button
                                    key={v.id}
                                    className={`sched-version-chip ${v.id === selectedVersionId ? "active" : ""} state-${versionState(v)}`}
                                    onClick={() => setSelectedVersionId(v.id)}
                                >
                                    {versionLabel(v)}
                                </button>
                            ))}
                        </div>
                    )}
                </div>

                {versionsLoading ? (
                    <LoadingSpinner message="Loading schedule" color="#e0e0e0" inline />
                ) : selectedVersion ? (
                    <>
                        {/* "Ended" is the admin's reason and the wrong words for a barber looking at the
                            hours he's working right now, so the message follows whichever reason applies.
                            A barber viewing a genuinely ended version gets that told to them too. */}
                        {isReadOnly && (
                            <p className="sched-readonly-note">
                                {versionState(selectedVersion) === "ended"
                                    ? "This schedule has ended — it's shown for reference and can't be edited."
                                    : "Your working hours are set by your manager — this is a read-only view."}
                            </p>
                        )}

                        <div className="sched-week" ref={weekRef}>
                            {DAYS.map((name, dow) => (
                                <div className="sched-day" key={dow}>
                                    <div className="sched-day-name">{name}</div>
                                    <div className="sched-day-shifts">
                                        {days[dow].length === 0 && <span className="sched-dayoff">Day off</span>}
                                        {days[dow].map((r, idx) => (
                                            <div className="sched-shift" key={idx}>
                                                {/* min/max come from the shop's hours for THIS weekday - the same bound
                                                    the save enforces, shown before the admin commits to a time. */}
                                                <input type="time" step={shiftStepSeconds} value={r.start} disabled={isReadOnly}
                                                    min={hoursForDay(dow)?.openTime} max={hoursForDay(dow)?.closeTime}
                                                    {...timeFieldProps}
                                                    onChange={(e) => { setShift(dow, idx, "start", e.target.value); onTimeChange(e); }} />
                                                <span>–</span>
                                                <input type="time" step={shiftStepSeconds} value={r.end} disabled={isReadOnly}
                                                    min={hoursForDay(dow)?.openTime} max={hoursForDay(dow)?.closeTime}
                                                    {...timeFieldProps}
                                                    onChange={(e) => { setShift(dow, idx, "end", e.target.value); onTimeChange(e); }} />
                                                {!isReadOnly && (
                                                    <button className="sched-icon-btn" title="Remove shift" onClick={() => removeShift(dow, idx)}>
                                                        <Trash2 size={15} />
                                                    </button>
                                                )}
                                            </div>
                                        ))}
                                        {/* A day the shop never opens can hold no shift, so say so instead of offering
                                            an "Add shift" that the save is guaranteed to refuse. */}
                                        {!isReadOnly && hoursForDay(dow)?.isClosed && (
                                            <span className="sched-dayoff">Shop closed — set opening hours in Settings</span>
                                        )}
                                        {!isReadOnly && !hoursForDay(dow)?.isClosed && (
                                            <button className="sched-add-shift" onClick={() => addShift(dow)}>
                                                <Plus size={14} /> Add shift
                                            </button>
                                        )}
                                    </div>
                                </div>
                            ))}
                        </div>

                        {shiftError && !isReadOnly && <p className="sched-error">{shiftError}</p>}

                        {!isReadOnly && (
                            <div className="sched-actions">
                                {/* Wrapped, not passed bare: handleSave's first parameter is confirmOrphaned,
                                    so onClick={handleSave} handed it React's click event. That made the
                                    request body circular, JSON.stringify threw inside axios, and the save
                                    never reached the server - it just showed a generic error toast. */}
                                <button className="btn-primary" onClick={() => handleSave()} disabled={saving || !!shiftError}>
                                    <Save size={16} /> {saving ? "Saving…" : "Save changes"}
                                </button>
                                {canDelete && (
                                    /* Wrapped for the same reason as the save button above: handleDelete's
                                       first parameter is now confirmOrphaned, so passing it bare would send
                                       React's click event as the confirmation flag. */
                                    <button className="btn-secondary sched-delete" onClick={() => handleDelete()} disabled={saving}>
                                        <Trash2 size={16} /> Remove this schedule
                                    </button>
                                )}
                            </div>
                        )}

                        {/* Sits OUTSIDE the !isReadOnly block above on purpose - an admin can start a new
                            season from an ended version as easily as from the current one. But it is still
                            a write, so a barber must not see it. */}
                        {!isBarber && (
                        <div className="sched-new">
                            <h3><CalendarPlus size={18} /> Schedule a seasonal change</h3>
                            <p>Create a new set of hours that takes over from a future date. It starts as a copy of the hours above — edit and save it after it's created. The latest schedule automatically ends the day before.</p>
                            {/* Only worth saying when it isn't just "not in the past" - i.e. when a change is
                                already queued and IT is what sets the floor. Wording deliberately echoes the
                                backend's rejection message so the two can't appear to disagree. */}
                            {openEndedVersion && dayAfterLatest > todayStr() && (
                                <p className="sched-new-floor">
                                    The latest scheduled version begins on {fmtDate(openEndedVersion.effectiveFrom)},
                                    so a new change can only start from {fmtDate(earliestNewFrom)}.
                                </p>
                            )}
                            <div className="sched-new-row">
                                <label className="sched-field">
                                    <span>Starts from</span>
                                    <input type="date" min={earliestNewFrom} value={newFromValue}
                                        onChange={(e) => setNewFrom(e.target.value)} />
                                </label>
                                {/* Same reason as the save button above - confirmOrphaned must not be the click event. */}
                                <button className="btn-primary" onClick={() => handleCreateVersion()} disabled={creating || !!shiftError || !newFromValue}>
                                    {creating ? "Creating…" : "Create change"}
                                </button>
                            </div>
                        </div>
                        )}
                    </>
                ) : null}
            </div>

            {orphanConflict && (
                <div className="modal-overlay" onClick={() => setOrphanConflict(null)}>
                    <div className="sched-orphan-modal" onClick={(e) => e.stopPropagation()}>
                        <h2>
                            {orphanConflict.kind === "delete"
                                ? "Bookings outside the restored hours"
                                : "Bookings outside the new hours"}
                        </h2>
                        <p>{orphanConflict.message}</p>
                        <ul className="sched-orphan-list">
                            {orphanConflict.affected.map((b) => (
                                <li key={b.id}>
                                    <span className="sched-orphan-when">{b.date} · {b.time}</span>
                                    <span className="sched-orphan-who">
                                        {b.customer || "Customer"}{b.phone ? ` · ${formatPhone(b.phone)}` : b.email ? ` · ${b.email}` : ""}
                                    </span>
                                </li>
                            ))}
                        </ul>
                        <div className="sched-orphan-actions">
                            <button className="btn-secondary" onClick={() => setOrphanConflict(null)} disabled={saving || creating}>
                                Go back
                            </button>
                            <button
                                className="btn-primary"
                                disabled={saving || creating}
                                onClick={() => {
                                    if (orphanConflict.kind === "save") return handleSave(true);
                                    if (orphanConflict.kind === "delete") return handleDelete(true);
                                    return handleCreateVersion(true);
                                }}
                            >
                                {saving || creating
                                    ? "Saving…"
                                    : orphanConflict.kind === "delete" ? "Remove anyway" : "Save anyway"}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* Counterpart to the modal above: those hours stranded bookings, these ones rescued some. */}
            {backInside && (
                <div className="modal-overlay" onClick={() => setBackInside(null)}>
                    <div className="sched-orphan-modal" onClick={(e) => e.stopPropagation()}>
                        <h2>These bookings fit again</h2>
                        <p>
                            {backInside.length === 1 ? "This booking was" : "These bookings were"} flagged for review
                            because {backInside.length === 1 ? "it" : "they"} fell outside this barber's hours, and the
                            hours you just saved cover {backInside.length === 1 ? "it" : "them"} again. Nothing has been
                            cleared for you — open <strong>Needs Review</strong> and read each note. If the schedule was
                            the only reason it was flagged, mark it as reviewed. If the note mentions anything else, such
                            as a refund to sort out or a customer to phone, deal with that first.
                        </p>
                        <ul className="sched-orphan-list">
                            {backInside.map((b) => (
                                <li key={b.id}>
                                    <span className="sched-orphan-when">#{b.id} · {b.date} · {b.time}</span>
                                    <span className="sched-orphan-who">
                                        {b.customer || "Customer"}{b.phone ? ` · ${formatPhone(b.phone)}` : b.email ? ` · ${b.email}` : ""}
                                    </span>
                                    {/* Saves the admin working out for themselves whether the slot is actually
                                        clear now. Silent when it is — only the exceptions are worth a line. */}
                                    {b.stillBlockedBy && (
                                        <span className="sched-orphan-blocked">Still blocked: {b.stillBlockedBy}</span>
                                    )}
                                </li>
                            ))}
                        </ul>
                        <div className="sched-orphan-actions">
                            <button className="btn-primary" onClick={() => setBackInside(null)}>Got it</button>
                        </div>
                    </div>
                </div>
            )}
        </>
    );
};

export default Schedules;
