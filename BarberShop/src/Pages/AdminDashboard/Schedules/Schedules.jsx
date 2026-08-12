import { useState, useEffect, useContext, useCallback, useRef } from "react";
import { useSearchParams } from "react-router-dom";
import { Plus, Trash2, Save, CalendarPlus, X } from "lucide-react";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import useFetch from "../../../Hooks/useFetch";
import { ToastContext } from "../../../Context/ToastContext";
import { AuthContext } from "../../../Context/AuthContext";
import LoadingSpinner from "../../../Components/LoadingSpinner/LoadingSpinner";
import ErrorState from "../../../Components/ErrorState/ErrorState";
import { getErrorMessage } from "../../../utils/errorMessage.js";
import { formatPhone } from "../../../utils/phone.js";
import { toMin, toHHMM, FALLBACK_OPEN_MIN, FALLBACK_CLOSE_MIN } from "../../../utils/time.js";
// Were defined here; shared so the Settings hours-conflict list names a version in the same words as
// the chips below, since its whole job is to tell the admin which chip to open.
import { todayStr, fmtDate, versionState, versionLabel } from "../../../utils/scheduleVersion.js";
import useTimeFieldFlow from "../../../Hooks/useTimeFieldFlow";
import "./Schedules.css";

const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
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
    /* "Schedule a seasonal change" is a modal opened from the page header, not the card it used to be at
       the foot of the page: that card sat BELOW the seven-day grid, so starting a new season meant
       scrolling past the whole week to find it. Same placement as the create actions on Team and
       Services. */
    const [showNewVersion, setShowNewVersion] = useState(false);
    /* Removing a version deletes its shifts outright (the DB cascades) and can't be undone, and the only
       thing that ever stopped to ask was the orphaned-bookings 409 - which only fires when bookings happen
       to be in the way. Otherwise a single click destroyed a version silently. Riskier now that the button
       sits in the header beside the benign "Schedule a change", where a misclick is easy. */
    const [confirmDelete, setConfirmDelete] = useState(false);
    // Mousedown target, so a drag that starts inside the modal and releases on the backdrop doesn't
    // close it - the same guard the Team and Services create modals use.
    const newVersionOverlayRef = useRef(null);
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
            /* Checked against what actually came back rather than trusted. This id can arrive from the URL
               (the Settings hours-conflict link), so it may be stale, edited, or another barber's - and
               selecting a version that isn't in the list leaves the editor on a blank week with every
               control disabled and nothing explaining why. Ids are unique across barbers, so a mismatch
               simply falls through to the version in force today. */
            const preferred = res.data.some((v) => v.id === preferVersionId) ? preferVersionId : null;
            const pick = preferred ?? active?.id ?? res.data[res.data.length - 1]?.id ?? null;
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

    /* Which version to open on, handed over by the Settings hours-conflict link. That message names one
       specific schedule - often a seasonal change that hasn't started yet - so landing on the version in
       force today would show the offending day as already correct, which is the confusion the link exists
       to remove.

       A ref read at mount, not state read in the effect: the effect must keep its two dependencies, and
       this is a handoff rather than a standing preference. Spent on first use, so picking another barber
       afterwards behaves normally (loadVersions would ignore it anyway - it validates the id against that
       barber's own versions). */
    const linkedVersionId = useRef(Number(searchParams.get("versionId")) || null);

    useEffect(() => {
        if (barberId == null) return;
        loadVersions(barberId, linkedVersionId.current);
        linkedVersionId.current = null;
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

    /* The free spans on a weekday: the shop's window for that day with every existing shift cut out of it.
     *
     * Real interval subtraction rather than "start after the last shift", because a stored shift can
     * already sit partly OUTSIDE the shop's hours - the hours may have moved since it was rostered - and
     * assuming the shifts are contained in the window would hand back a span that is already occupied.
     *
     * Shifts that are themselves broken (blank, or start >= end) are ignored rather than subtracted: they
     * describe no interval, and treating a backwards one as a range would blank out the wrong part of the
     * day. They already carry their own error in the row. */
    const freeGaps = (dow, ranges) => {
        const hours = hoursForDay(dow);
        const open = hours && !hours.isClosed ? toMin(hours.openTime) : FALLBACK_OPEN_MIN;
        const close = hours && !hours.isClosed ? toMin(hours.closeTime) : FALLBACK_CLOSE_MIN;
        if (close <= open) return [];

        const taken = ranges
            .filter((r) => r.start && r.end && r.start < r.end)
            .map((r) => [toMin(r.start), toMin(r.end)])
            .sort((a, b) => a[0] - b[0]);

        const gaps = [];
        let cursor = open;
        for (const [s, e] of taken) {
            if (s > cursor) gaps.push([cursor, Math.min(s, close)]);
            cursor = Math.max(cursor, e);
            if (cursor >= close) break;
        }
        if (cursor < close) gaps.push([cursor, close]);
        return gaps.filter(([s, e]) => e > s);
    };

    /* The widest free span, which is the one the admin means in every realistic split-shift case: with
     * 09:00-13:00 rostered the only gap is the afternoon, with 14:00-17:30 it's the morning, and with a
     * midday shift it's the longer of the two sides rather than whichever happens to come first.
     *
     * No minimum size. A five-minute gap yields a five-minute shift, which is silly but legal - the server
     * only asks that a shift start before it ends. Requiring more would be a rule the editor invented and
     * nothing else enforces, the same trap the shiftStepSeconds note above describes. */
    const largestGap = (dow, ranges) => {
        const gaps = freeGaps(dow, ranges);
        if (gaps.length === 0) return null;
        return gaps.reduce((best, g) => (g[1] - g[0] > best[1] - best[0] ? g : best));
    };

    /* A new shift fills the largest free gap instead of the shop's whole day. Seeding it with the full day
     * meant that on any day that already had a shift, the very act of adding one produced an overlap error
     * - the editor handed you a broken grid and then told you off for it.
     *
     * Sorted on insert, not appended: shifts arrive sorted (see versionToDays) and a morning shift added
     * after an afternoon one would otherwise render out of order. */
    const addShift = (dow) => {
        setDays((prev) => prev.map((r, i) => {
            if (i !== dow) return r;
            const gap = largestGap(dow, r);
            if (!gap) return r; // no room; the button is replaced by a reason in this state
            return [...r, { start: toHHMM(gap[0]), end: toHHMM(gap[1]) }]
                .sort((a, b) => a.start.localeCompare(b.start));
        }));
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


    /* Client mirror of ShopHoursResolver.ShiftViolation - the same four reasons in the same words, so an
     * inline message here and the server's rejection can't drift into disagreeing about what fits. Both
     * sides carry "HH:mm" (SettingsController.HoursPayload formats them that way), so comparing these as
     * strings is comparing them as times. */
    const shopHoursViolation = (dow, start, end) => {
        const h = hoursForDay(dow);
        if (!h) return "the shop has no hours set for this day";
        if (h.isClosed) return "the shop is closed on this day";
        if (start < h.openTime) return `it starts before the shop opens (${h.openTime})`;
        if (end > h.closeTime) return `it ends after the shop closes (${h.closeTime})`;
        return null;
    };

    /* One message per offending SHIFT, indexed to match days[dow], so each one prints in its own day's row
     * and outlines its own field. The week is seven tall rows: a single error under the whole grid sits off
     * the bottom of the screen while you're typing in Friday, which is what made it useless.
     *
     * The shop-hours rule used to arrive as a browser tooltip, from min/max on the time inputs. Native
     * constraint validation draws its own bubble - unstyleable, worded by the browser rather than by us,
     * and gone the moment the field loses focus, so the reason disappeared exactly when the admin turned to
     * fix it. The bound it was communicating is now named in the message instead.
     *
     * Mirrors the backend's two checks in the order it runs them: ValidateShifts (start before end, no
     * overlap), then ValidateShiftsWithinShopHours. The shop-hours half is SKIPPED until /api/Settings has
     * answered - with no hours loaded every day would read "the shop has no hours set" on first paint, and
     * Save would be disabled before the admin had done anything wrong. */
    const hoursLoaded = shopHours.length > 0;
    // Takes the day-array to judge rather than closing over `days`: the editor's draft and the version's
    // SAVED shifts are both run through it, and they can disagree (see savedDayErrors below).
    const computeDayErrors = (source) => source.map((ranges, dow) => {
        const errors = ranges.map(() => null);
        // Overlap is a question about the order shifts RUN in, so it's judged on a sorted copy - but each
        // message is filed against the shift's ORIGINAL index, which is the one the row renders.
        const order = ranges.map((r, idx) => ({ ...r, idx })).sort((a, b) => a.start.localeCompare(b.start));
        order.forEach((r, i) => {
            if (!r.start || !r.end || r.start >= r.end) {
                errors[r.idx] = "This shift must start before it ends.";
                return;
            }
            if (i > 0 && r.start < order[i - 1].end) {
                errors[r.idx] = `This shift overlaps the one ending at ${order[i - 1].end}.`;
                return;
            }
            const reason = hoursLoaded ? shopHoursViolation(dow, r.start, r.end) : null;
            if (reason) errors[r.idx] = `${r.start}–${r.end} doesn't fit the shop's opening hours: ${reason}.`;
        });
        return errors;
    });
    const dayErrors = computeDayErrors(days);
    const hasShiftError = dayErrors.some((e) => e.some(Boolean));

    /* What "Schedule a change" copies: the version's SAVED shifts, not the editor's draft.
     *
     * It used to post `days`, which meant a half-finished edit nobody had committed was silently baked
     * into a brand-new season - and the orphan check then warned about bookings stranded by hours that
     * existed nowhere but this browser tab. Worse since the control became a modal: the week is behind
     * the overlay, so the admin can't even see what's being copied. The two writes have separate
     * buttons and should stay separate - Save edits this version, Create starts a new one from what
     * this version actually says. */
    const savedDays = versionToDays(selectedVersion);

    /* The create's own gate, judged on what it copies. For a current or future version this can never
     * fire: UpdateShopHours refuses any hours change that would leave a live schedule outside them, with
     * no override. It stays because that veto has two deliberate exclusions, and both reach this screen:
     *   - ENDED versions ("refusing an hours change over a schedule nobody works any more would be
     *     unfixable"), which an admin may still copy a new season from; and
     *   - a barber who was DEACTIVATED while the hours moved, whose current version can be outside them
     *     by the time they're reactivated and selectable again.
     * The two need different messages - see the modal - because only the second is fixable in place. */
    const savedHasShiftError = computeDayErrors(savedDays).some((e) => e.some(Boolean));

    /* Compared on a sorted copy: versionToDays sorts, and addShift keeps the editor sorted on insert, but
       retyping a start time can leave `days` out of order without changing what it means. */
    const sameShifts = (a, b) => a.every((ranges, dow) => {
        const x = [...ranges].sort((p, q) => p.start.localeCompare(q.start));
        const y = [...b[dow]].sort((p, q) => p.start.localeCompare(q.start));
        return x.length === y.length && x.every((r, i) => r.start === y[i].start && r.end === y[i].end);
    });
    // Drives the modal's heads-up. Silently ignoring the draft would swap one surprise for another.
    const editorDirty = selectedVersion != null && !sameShifts(days, savedDays);

    // Back to the version's saved shifts - the same seed the version-change effect uses, so discarding and
    // reselecting the chip land on identical state.
    const handleDiscard = () => setDays(versionToDays(selectedVersion));

    const handleSave = async (confirmOrphaned = false) => {
        if (hasShiftError || isReadOnly || !selectedVersion) return;
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
        if (creating || isBarber || !selectedVersion || savedHasShiftError) return;
        setCreating(true);
        try {
            // Seeded from the selected version's SAVED shifts (see savedDays) so the admin refines from
            // hours the shop has actually committed to, rather than a blank week OR an uncommitted draft.
            const res = await adminAxios.post(`/api/Schedules/barber/${barberId}`, {
                effectiveFrom: newFromValue,
                shifts: daysToShifts(savedDays),
                confirmOrphaned,
            });
            setOrphanConflict(null);
            setShowNewVersion(false);
            showToast(
                "Schedule change created",
                `New hours take effect from ${fmtDate(newFromValue)}. The previous schedule ends the day before.`,
                "success");
            announceBackInsideHours(res.data?.backInsideHours);
            await loadVersions(barberId, res.data.id);
        } catch (err) {
            if (err.response?.status === 409 && err.response.data?.requiresConfirmation) {
                /* Stand this modal down as the conflict one takes over - otherwise the two stack, and
                   "Save anyway" would return to a create dialog for a change that has just been made.
                   That button calls handleCreateVersion(true), which no longer needs this open. */
                setShowNewVersion(false);
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
        // Stood down here rather than in the click handler so the 409 path closes it too - otherwise the
        // orphan modal would stack on top of a confirmation for a delete already in flight.
        setConfirmDelete(false);
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
            {/* Not page-header--inline (nowrap) any more: this header carries TWO buttons now, and pinning
                them to the title's line squeezed both to unreadable widths on a phone. The wrapping base
                header drops .page-header-actions under the title as a group instead. */}
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
                {/* Gated the same way the card it replaced was: hidden from barbers (it's a write), and
                    only once a version is on screen, since the new season is seeded from it. Deliberately
                    NOT gated on isReadOnly - an admin can start a new season from an ended version just as
                    easily as from the current one. */}
                {!isBarber && !versionsLoading && selectedVersion && (
                    <div className="page-header-actions">
                        <button
                            className="create-barber"
                            onClick={() => { setNewFrom(earliestNewFrom); setShowNewVersion(true); }}
                        >
                            <CalendarPlus size={16} />
                            SCHEDULE A CHANGE
                        </button>
                        {/* Was at the very foot of the page under the seven day rows, which is exactly
                            where "Schedule a change" used to be and was moved from for the same reason:
                            you couldn't see it without scrolling past the whole week. Second of the pair,
                            so the routine action reads first and the destructive one doesn't lead. It
                            keeps the outline treatment rather than a second solid fill, which would make
                            the two look equally encouraged. */}
                        {canDelete && (
                            <button
                                className="sched-remove"
                                onClick={() => setConfirmDelete(true)}
                                disabled={saving}
                            >
                                <Trash2 size={16} />
                                {/* "Remove this schedule" is the wrong words for a season that hasn't
                                    started - nothing has been worked under it, so there's nothing to
                                    remove yet; you're calling off a plan. */}
                                {versionState(selectedVersion) === "upcoming"
                                    ? "CANCEL THIS CHANGE"
                                    : "REMOVE THIS SCHEDULE"}
                            </button>
                        )}
                    </div>
                )}
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

                {/* Why the Remove button isn't in the header on this chip. Without it the button simply
                    vanishes as you click along the timeline, which reads as a bug rather than a rule -
                    and the rule isn't guessable: removal is an UNDO of the newest change, so it walks
                    backwards from the end rather than picking any version off the row. */}
                {!isBarber && !versionsLoading && selectedVersion && !canDelete && (versions ?? []).length > 1 && (
                    <p className="sched-remove-note">
                        Only the latest schedule can be removed — that restores the one before it. Earlier
                        schedules are kept as a record of the hours already worked.
                    </p>
                )}

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
                                        {days[dow].map((r, idx) => {
                                            /* Shown to the admin on an ENDED version too, not just an editable one.
                                               An ended version's shifts can genuinely stop fitting the shop's hours
                                               once those hours move, and the admin still needs to see it: "Create
                                               change" seeds the new season from whatever's in the editor, and
                                               hasShiftError disables that button - so hiding the reason here would
                                               leave a dead button with nothing on screen explaining it.
                                               A barber is the one viewer this stays off for: they can't act on it,
                                               and the read-only note already tells them whose job it is. */
                                            const invalid = !isBarber && !!dayErrors[dow][idx];
                                            return (
                                            /* No min/max on these deliberately: the shop's bound is enforced by
                                               dayErrors and printed under this row. Left to the browser it came back
                                               as a native validation bubble - see the comment on dayErrors. */
                                            <div className={`sched-shift${invalid ? " sched-shift--invalid" : ""}`} key={idx}>
                                                <input type="time" step={shiftStepSeconds} value={r.start} disabled={isReadOnly}
                                                    aria-invalid={invalid}
                                                    {...timeFieldProps}
                                                    onChange={(e) => { setShift(dow, idx, "start", e.target.value); onTimeChange(e); }} />
                                                <span>–</span>
                                                <input type="time" step={shiftStepSeconds} value={r.end} disabled={isReadOnly}
                                                    aria-invalid={invalid}
                                                    {...timeFieldProps}
                                                    onChange={(e) => { setShift(dow, idx, "end", e.target.value); onTimeChange(e); }} />
                                                {!isReadOnly && (
                                                    <button className="sched-icon-btn" title="Remove shift" onClick={() => removeShift(dow, idx)}>
                                                        <Trash2 size={15} />
                                                    </button>
                                                )}
                                            </div>
                                            );
                                        })}
                                        {/* A day the shop never opens can hold no shift, so say so instead of offering
                                            an "Add shift" that the save is guaranteed to refuse. */}
                                        {!isReadOnly && hoursForDay(dow)?.isClosed && (
                                            <span className="sched-dayoff">Shop closed — set opening hours in Settings</span>
                                        )}
                                        {/* Same treatment as the closed-day line above: when a shift can't be
                                            added, say why instead of offering a button that would produce a
                                            broken row. Here the day's shifts already cover every open
                                            minute, so there is nowhere left to put one. */}
                                        {!isReadOnly && !hoursForDay(dow)?.isClosed && (
                                            largestGap(dow, days[dow]) == null ? (
                                                <span className="sched-dayoff">No free time left in the shop's hours</span>
                                            ) : (
                                                <button className="sched-add-shift" onClick={() => addShift(dow)}>
                                                    <Plus size={14} /> Add shift
                                                </button>
                                            )
                                        )}
                                    </div>
                                    {/* Third grid child, pinned under this day's shifts rather than under the whole
                                        week - see the comment on dayErrors for why that placement is the point. */}
                                    {!isBarber && dayErrors[dow].some(Boolean) && (
                                        <div className="sched-day-errors">
                                            {dayErrors[dow].map((msg, idx) => msg && (
                                                <span className="form-error" key={idx}>{msg}</span>
                                            ))}
                                        </div>
                                    )}
                                </div>
                            ))}
                        </div>

                        {/* Was a plain row of buttons at the foot of the page. The week is seven tall rows,
                            so editing Monday put the only way to save it below the fold with nothing on
                            screen saying it existed - the same problem the Settings save bar was built for,
                            and it now shares those styles. Only rendered against an actual unsaved edit, so
                            reading a barber's hours costs no vertical space. */}
                        {!isReadOnly && editorDirty && (
                            <div className="savebar" role="region" aria-label="Unsaved changes">
                                <div className="savebar__text">
                                    <strong>Unsaved changes</strong>
                                    {/* The offending shift can be several rows up by the time the bar is
                                        read, so say why Save is dead rather than leaving it looking broken. */}
                                    {hasShiftError && (
                                        <span className="savebar__hint savebar__hint--error">
                                            Fix the highlighted shifts before saving.
                                        </span>
                                    )}
                                </div>
                                <div className="savebar__actions">
                                    <button type="button" className="btn-secondary" onClick={handleDiscard} disabled={saving}>
                                        Discard
                                    </button>
                                    {/* Wrapped, not passed bare: handleSave's first parameter is confirmOrphaned,
                                        so onClick={handleSave} handed it React's click event. That made the
                                        request body circular, JSON.stringify threw inside axios, and the save
                                        never reached the server - it just showed a generic error toast. */}
                                    <button className="btn-primary" onClick={() => handleSave()} disabled={saving || hasShiftError}>
                                        <Save size={16} /> {saving ? "Saving…" : "Save changes"}
                                    </button>
                                </div>
                            </div>
                        )}

                    </>
                ) : null}
            </div>

            {/* Was a permanent card below the week grid; it's the header button's modal now. Everything it
                said is still said, just at the moment the admin asks for it instead of costing a scroll
                past seven day-rows on every visit. */}
            {showNewVersion && !isBarber && selectedVersion && (
                <div className="modal-overlay"
                    onMouseDown={(e) => (newVersionOverlayRef.current = e.target)}
                    onClick={(e) => { if (newVersionOverlayRef.current === e.currentTarget) setShowNewVersion(false); }}>
                    <div className="modal-content" onClick={(e) => e.stopPropagation()}>
                        <div className="modal-header">
                            <h2>Schedule a seasonal change</h2>
                            <button className="modal-close" onClick={() => setShowNewVersion(false)}><X size={20} /></button>
                        </div>
                        <div className="modal-body">
                            {/* "the currently selected schedule", not "the hours above": the week grid is behind
                                this modal rather than above it, and naming the selection is what makes it clear
                                WHICH version is being copied when several chips are on screen. The copy is taken
                                from whatever is in the editor - including edits that haven't been saved yet. */}
                            <p className="sched-new-intro">
                                Create a new set of hours that takes over from a future date. It starts as a copy
                                of the hours present in the currently selected schedule — edit and save it after
                                it's created. The latest schedule automatically ends the day before.
                            </p>
                            {/* Only worth saying when it isn't just "not in the past" - i.e. when a change is
                                already queued and IT is what sets the floor. Wording deliberately echoes the
                                backend's rejection message so the two can't appear to disagree. */}
                            {openEndedVersion && dayAfterLatest > todayStr() && (
                                <p className="sched-new-floor">
                                    The latest scheduled version begins on {fmtDate(openEndedVersion.effectiveFrom)},
                                    so a new change can only start from {fmtDate(earliestNewFrom)}.
                                </p>
                            )}
                            <label className="sched-field">
                                <span>Starts from</span>
                                <input type="date" min={earliestNewFrom} value={newFromValue}
                                    onChange={(e) => setNewFrom(e.target.value)} />
                            </label>
                            {/* Unsaved edits are NOT copied (see savedDays), so say so rather than letting the
                                admin assume the draft behind this overlay is what's being branched from. */}
                            {editorDirty && !savedHasShiftError && (
                                <p className="sched-new-note">
                                    You have unsaved changes to this schedule. The new schedule is copied from
                                    the <strong>saved</strong> hours, not your current edits — save them first
                                    if you want them carried over.
                                </p>
                            )}
                            {/* Why "Create change" is dead. Two different situations, and only one of them is
                                something the admin can act on from this screen - an ended version's shifts are
                                read-only, so telling them to go and fix the highlighted rows would be advice
                                they cannot take. */}
                            {savedHasShiftError && (
                                <p className="sched-new-hint">
                                    {isReadOnly
                                        ? `These hours no longer fit the shop's opening hours, so they can't be
                                           copied into a new schedule. Pick a different schedule to copy from, or
                                           update the shop's opening hours in Settings.`
                                        : `Fix the highlighted shifts and save them before creating a change —
                                           the new schedule is copied from this schedule's saved hours.`}
                                </p>
                            )}
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={() => setShowNewVersion(false)} disabled={creating}>
                                Cancel
                            </button>
                            {/* Same reason as the save button - confirmOrphaned must not be the click event. */}
                            <button className="btn-primary" onClick={() => handleCreateVersion()} disabled={creating || savedHasShiftError || !newFromValue}>
                                {creating ? "Creating…" : "Create change"}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* Deliberately says nothing about whether the restored hours still fit the shop's opening
                hours - they may not, since ended versions are exempt from the UpdateShopHours veto. That
                warning wouldn't change the answer (you want the version gone either way, and the restored
                one is editable the moment it's back), and the editor prints the real thing straight after:
                per-day errors naming the exact violation on the exact rows. A confirmation line nobody acts
                on only teaches people to click through the one that matters - the orphaned-bookings modal
                below, which genuinely should stop them. */}
            {confirmDelete && canDelete && (
                <div className="modal-overlay" onClick={() => setConfirmDelete(false)}>
                    {/* Its own class, not .sched-orphan-modal, even though they share a surface: this is the
                        "are you sure" step and that one is the bookings-in-the-way step, and they can appear
                        back to back in the same flow. One class for both would make them indistinguishable
                        to anything selecting on it - which is exactly what the E2E specs do. */}
                    <div className="sched-confirm-modal" onClick={(e) => e.stopPropagation()}>
                        <h2>
                            {versionState(selectedVersion) === "upcoming"
                                ? "Cancel this scheduled change?"
                                : "Remove this schedule?"}
                        </h2>
                        {/* Phrased as an ending that stops happening, not as a date something starts on.
                            "Takes over from 10 Aug onwards" read as though the 9th were left uncovered -
                            it never is, the previous schedule already governed that day and simply carries
                            on through it. Saying which end date is being cancelled describes the actual
                            change, and "no gap in cover" closes the misreading outright.

                            Both dates come from the version being REMOVED (its start, minus a day for the
                            predecessor's end, which the contiguous chain guarantees). Never from the
                            restored version itself: its label ("Schedule until 30 Jun") stops being true
                            the instant it reopens open-ended, and its effectiveFrom can be the 2020-01-01
                            sentinel the back-fill migration gives every pre-existing barber - a date
                            chosen to sit safely in the past, not one to show anybody. */}
                        <p>
                            <strong>{versionLabel(selectedVersion)}</strong> and its shifts will be deleted.
                            The schedule before it no longer ends on{" "}
                            {fmtDate(addDays(selectedVersion.effectiveFrom, -1))} — it carries straight on
                            as this barber's current schedule, with no gap in cover. This can't be undone.
                        </p>
                        <div className="sched-orphan-actions">
                            <button className="btn-secondary" onClick={() => setConfirmDelete(false)} disabled={saving}>
                                Keep it
                            </button>
                            {/* Wrapped for the same reason as the save button: handleDelete's first
                                parameter is confirmOrphaned, so passing it bare would send React's click
                                event as the confirmation flag. */}
                            <button className="sched-remove" onClick={() => handleDelete()} disabled={saving}>
                                {saving
                                    ? "Removing…"
                                    : versionState(selectedVersion) === "upcoming" ? "Cancel change" : "Remove schedule"}
                            </button>
                        </div>
                    </div>
                </div>
            )}

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
