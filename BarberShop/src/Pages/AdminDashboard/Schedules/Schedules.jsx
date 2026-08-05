import { useState, useEffect, useContext, useCallback } from "react";
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
import "./Schedules.css";

const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const todayStr = () => {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};
const fmtDate = (iso) => new Date(iso + "T00:00:00").toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });

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
const versionLabel = (v) => {
    const s = versionState(v);
    if (s === "active") return "Active now";
    if (s === "upcoming") return `Upcoming — from ${fmtDate(v.effectiveFrom)}`;
    return `Ended — until ${fmtDate(v.effectiveTo)}`;
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

    const addShift = (dow) => {
        setDays((prev) => prev.map((r, i) => (i === dow ? [...r, { start: "09:00", end: "17:30" }] : r)));
    };
    const removeShift = (dow, idx) => {
        setDays((prev) => prev.map((r, i) => (i === dow ? r.filter((_, j) => j !== idx) : r)));
    };
    const setShift = (dow, idx, field, value) => {
        setDays((prev) => prev.map((r, i) => (i === dow ? r.map((s, j) => (j === idx ? { ...s, [field]: value } : s)) : r)));
    };

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
                effectiveFrom: newFrom,
                shifts: daysToShifts(days),
                confirmOrphaned,
            });
            setOrphanConflict(null);
            showToast("Schedule change created", `New hours take effect from ${fmtDate(newFrom)}.`, "success");
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
                            : "Set each barber's working hours, split shifts and seasonal changes"}
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

                        <div className="sched-week">
                            {DAYS.map((name, dow) => (
                                <div className="sched-day" key={dow}>
                                    <div className="sched-day-name">{name}</div>
                                    <div className="sched-day-shifts">
                                        {days[dow].length === 0 && <span className="sched-dayoff">Day off</span>}
                                        {days[dow].map((r, idx) => (
                                            <div className="sched-shift" key={idx}>
                                                <input type="time" step={1800} value={r.start} disabled={isReadOnly}
                                                    onChange={(e) => setShift(dow, idx, "start", e.target.value)} />
                                                <span>–</span>
                                                <input type="time" step={1800} value={r.end} disabled={isReadOnly}
                                                    onChange={(e) => setShift(dow, idx, "end", e.target.value)} />
                                                {!isReadOnly && (
                                                    <button className="sched-icon-btn" title="Remove shift" onClick={() => removeShift(dow, idx)}>
                                                        <Trash2 size={15} />
                                                    </button>
                                                )}
                                            </div>
                                        ))}
                                        {!isReadOnly && (
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
                            <p>Create a new set of hours that takes over from a future date. It starts as a copy of the hours above — edit and save it after it's created. The current schedule automatically ends the day before.</p>
                            <div className="sched-new-row">
                                <label className="sched-field">
                                    <span>Starts from</span>
                                    <input type="date" min={todayStr()} value={newFrom} onChange={(e) => setNewFrom(e.target.value)} />
                                </label>
                                {/* Same reason as the save button above - confirmOrphaned must not be the click event. */}
                                <button className="btn-primary" onClick={() => handleCreateVersion()} disabled={creating || !!shiftError || !newFrom}>
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
