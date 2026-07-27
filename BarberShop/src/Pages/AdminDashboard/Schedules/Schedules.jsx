import { useState, useEffect, useContext, useCallback } from "react";
import { useSearchParams } from "react-router-dom";
import { Plus, Trash2, Save, CalendarPlus } from "lucide-react";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import useFetch from "../../../Hooks/useFetch";
import { ToastContext } from "../../../Context/ToastContext";
import LoadingSpinner from "../../../Components/LoadingSpinner/LoadingSpinner";
import ErrorState from "../../../Components/ErrorState/ErrorState";
import { getErrorMessage } from "../../../utils/errorMessage.js";
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
    const { data: barbersData, loading: barbersLoading, error: barbersError } = useFetch("/api/Barbers/admin", true);
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

    const activeBarbers = (barbersData ?? []).filter((b) => b.isActive);
    const selectedVersion = (versions ?? []).find((v) => v.id === selectedVersionId) ?? null;
    const isReadOnly = selectedVersion ? versionState(selectedVersion) === "ended" : true;
    const canDelete = selectedVersion && selectedVersion.effectiveTo == null && (versions ?? []).length > 1;

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

    // Preselect a barber from ?barberId (the create-barber handoff) once the roster is in.
    useEffect(() => {
        if (barberId != null || activeBarbers.length === 0) return;
        const fromQuery = Number(searchParams.get("barberId"));
        const initial = activeBarbers.some((b) => b.id === fromQuery) ? fromQuery : activeBarbers[0].id;
        setBarberId(initial);
    }, [activeBarbers, barberId, searchParams]);

    useEffect(() => {
        if (barberId != null) loadVersions(barberId);
    }, [barberId, loadVersions]);

    // Seed the day editor whenever the selected version changes.
    useEffect(() => {
        setDays(versionToDays(selectedVersion));
    }, [selectedVersionId, versions]); // eslint-disable-line react-hooks/exhaustive-deps

    const onPickBarber = (id) => {
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
            await adminAxios.put(`/api/Schedules/version/${selectedVersion.id}`,
                { shifts: daysToShifts(days), confirmOrphaned });
            setOrphanConflict(null);
            showToast("Schedule saved", "The barber's working hours have been updated.", "success");
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
        if (creating) return;
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

    const handleDelete = async () => {
        if (!canDelete) return;
        setSaving(true);
        try {
            await adminAxios.delete(`/api/Schedules/version/${selectedVersion.id}`);
            showToast("Schedule removed", "Reverted to the previous schedule.", "success");
            await loadVersions(barberId);
        } catch (err) {
            showToast("Couldn't remove schedule", getErrorMessage(err));
        } finally {
            setSaving(false);
        }
    };

    if (barbersLoading) return <LoadingSpinner message="Loading schedules" color="#e0e0e0" />;
    if (barbersError) return <ErrorState title="Couldn't load barbers" message="Please retry." />;

    return (
        <>
            <div className="page-header">
                <div className="page-header-text">
                    <h1 className="page-title">Schedules</h1>
                    <p className="page-subtitle">Set each barber's working hours, split shifts and seasonal changes</p>
                </div>
            </div>

            <div className="sched-content">
                <div className="sched-toolbar">
                    <label className="sched-field">
                        <span>Barber</span>
                        <select value={barberId ?? ""} onChange={(e) => onPickBarber(Number(e.target.value))}>
                            {activeBarbers.map((b) => (
                                <option key={b.id} value={b.id}>{b.firstName} {b.lastName}</option>
                            ))}
                        </select>
                    </label>

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
                        {isReadOnly && (
                            <p className="sched-readonly-note">This schedule has ended — it's shown for reference and can't be edited.</p>
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
                                <button className="btn-primary" onClick={handleSave} disabled={saving || !!shiftError}>
                                    <Save size={16} /> {saving ? "Saving…" : "Save changes"}
                                </button>
                                {canDelete && (
                                    <button className="btn-secondary sched-delete" onClick={handleDelete} disabled={saving}>
                                        <Trash2 size={16} /> Remove this schedule
                                    </button>
                                )}
                            </div>
                        )}

                        <div className="sched-new">
                            <h3><CalendarPlus size={18} /> Schedule a seasonal change</h3>
                            <p>Create a new set of hours that takes over from a future date. It starts as a copy of the hours above — edit and save it after it's created. The current schedule automatically ends the day before.</p>
                            <div className="sched-new-row">
                                <label className="sched-field">
                                    <span>Starts from</span>
                                    <input type="date" min={todayStr()} value={newFrom} onChange={(e) => setNewFrom(e.target.value)} />
                                </label>
                                <button className="btn-primary" onClick={handleCreateVersion} disabled={creating || !!shiftError || !newFrom}>
                                    {creating ? "Creating…" : "Create change"}
                                </button>
                            </div>
                        </div>
                    </>
                ) : null}
            </div>

            {orphanConflict && (
                <div className="modal-overlay" onClick={() => setOrphanConflict(null)}>
                    <div className="sched-orphan-modal" onClick={(e) => e.stopPropagation()}>
                        <h2>Bookings outside the new hours</h2>
                        <p>{orphanConflict.message}</p>
                        <ul className="sched-orphan-list">
                            {orphanConflict.affected.map((b) => (
                                <li key={b.id}>
                                    <span className="sched-orphan-when">{b.date} · {b.time}</span>
                                    <span className="sched-orphan-who">
                                        {b.customer || "Customer"}{b.phone ? ` · ${b.phone}` : b.email ? ` · ${b.email}` : ""}
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
                                onClick={() => (orphanConflict.kind === "save" ? handleSave(true) : handleCreateVersion(true))}
                            >
                                {saving || creating ? "Saving…" : "Save anyway"}
                            </button>
                        </div>
                    </div>
                </div>
            )}
        </>
    );
};

export default Schedules;
