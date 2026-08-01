import { useState, useEffect, useContext, useRef } from "react";
import FullCalendar from "@fullcalendar/react";
import { formatDate } from "@fullcalendar/core";
import dayGridPlugin from "@fullcalendar/daygrid";
import timeGridPlugin from "@fullcalendar/timegrid";
import interactionPlugin from "@fullcalendar/interaction";
import listPlugin from "@fullcalendar/list";
import "./Calendar.css";
import useFetch from "../../../Hooks/useFetch.js";
import { Box, List, ListItem, ListItemText, Typography } from "@mui/material";
import { startOfDay, addDays } from "date-fns";
import { Trash2, X, Menu, Mail, Phone, AlertTriangle, RotateCw } from "lucide-react";
import { AuthContext } from "../../../Context/AuthContext.jsx";
import { ToastContext } from "../../../Context/ToastContext.jsx";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import { getErrorMessage } from "../../../utils/errorMessage.js";
import { formatPhone } from "../../../utils/phone.js";

const AdminCalendar = () => {
    const { user } = useContext(AuthContext);
    const { showToast } = useContext(ToastContext);
    const closuresUrl = user?.role === 'BARBER'
        ? `/api/dates/barber/${user?.barberId}/closures`
        : '/api/dates/admin/closures';
    const { data, loading, error, reFetch } = useFetch(closuresUrl, true);
    /* Admins can scope a closure to one barber; barbers only ever close their own time, so they don't
     * need (or get) this list. `/api/barbers/admin` includes deactivated barbers, so filter to active -
     * a deactivated barber takes no bookings, so closing their time would be meaningless. */
    const { data: barbersData } = useFetch(user?.role === "ADMIN" ? "/api/barbers/admin" : null, true);
    const activeBarbers = (barbersData ?? []).filter(b => b.isActive);
    // "" = whole shop (BarberId null); otherwise the selected barber's id.
    const [selectedBarberId, setSelectedBarberId] = useState("");
    const [deleteSelectedEvent, setDeleteSelectedEvent] = useState(null);
    const [events, setEvents] = useState([]);
    const [sidebarOpen, setSidebarOpen] = useState(false);
    /* Set when a closure POST comes back 409 because it overlaps existing bookings: holds the backend's
     * { message, conflicts } plus the original payload (closureData) so the admin can review who'd be
     * cancelled and re-submit with confirmCancelBookings. submitting guards the confirm button. */
    const [conflictData, setConflictData] = useState(null);
    const [submitting, setSubmitting] = useState(false);
    // Ref to the FullCalendar instance so clicking an event in the list can jump the grid to its date.
    const calendarRef = useRef(null);
    /* Holds the FullCalendar date-selection (the dragged range) while the "create closure" modal is open,
     * plus the reason the admin types. Replaces the old window.prompt(). Null = modal closed. */
    const [pendingSelection, setPendingSelection] = useState(null);
    const [closureReason, setClosureReason] = useState("");

    /* Clicking an event in the Events list navigates the calendar to the month/day that event falls in
     * (gotoDate keeps the current view type and just moves the date). Guarded because the API isn't
     * available until the calendar has mounted. Also closes the mobile drawer so the grid is visible. */
    const goToEventOnCalendar = (event) => {
        const api = calendarRef.current?.getApi();
        if (!api || !event?.start) return;
        api.gotoDate(event.start);
        setSidebarOpen(false);
    };
    useEffect(() => {
        if (!data) return;
        setEvents(data.map(closure => ({      
            id: closure.id,
            title: user?.role === "ADMIN" && closure.barberName ? `${closure.barberName} - ${closure.reason}` : closure.reason,
            start: closure.isFullDay
                ? closure.startDate
                : `${closure.startDate}T${closure.startTime}`,
            end: closure.isFullDay
                ? closure.endDate
                    ? addDays(closure.endDate, 1)  // multi-day: make end exclusive
                    : closure.startDate             // single day: just use startDate, FullCalendar handles it
                : `${closure.endDate ?? closure.startDate}T${closure.endTime}`,
            allDay: closure.isFullDay
        })));
        console.log(data);
    }, [data]);

    /* A failed load leaves the calendar showing no closures - which looks identical to "there are none".
       Creating a closure from that blind state risks duplicating/overlapping one the admin simply couldn't
       see, so while `error` is set we disable closure creation (see `selectable` + handleDateClick below)
       and show a persistent banner with Retry. The grid itself stays viewable/navigable. 401 is handled by
       the axios interceptor. */
    const loadFailed = !!error && error.response?.status !== 401;

    useEffect(() => {
        const handleResize = () => {
            if (window.innerWidth > 740) {
                setSidebarOpen(false);
            }
        };

        window.addEventListener('resize', handleResize);
        return () => window.removeEventListener('resize', handleResize);
    }, []);
    /* the above useEffect is for when the user resizes the window below 740, opens
    the events and then resizes the width above 740. If that situation happens, when the user resizes back to 740, the bottom drawer is closed
    */
    const now = new Date();
    const [pastHeightPercent, setPastHeightPercent] = useState(0);
    const [currentView, setCurrentView] = useState('dayGridMonth');
    /*const [currentViewDate, setCurrentViewDate] = useState(new Date());*/
    useEffect(() => {
        const totalMinutes = 24 * 60;
        const passedMinutes = (now.getHours() * 60) + now.getMinutes();
        setPastHeightPercent((passedMinutes / totalMinutes) * 100);
    }, [])

    const formatEventDate = (event) => {
        const rawEnd = event.end ? new Date(event.end) : null;//this might be redundant since when we fetch the closures we will event.end anyway

        const endDate = rawEnd && event.allDay && rawEnd > new Date(event.start)
            ? new Date(new Date(event.end).setDate(new Date(event.end).getDate() - 1))
            : rawEnd; 

    const startDate = new Date(event.start);

    const sameDay =  !endDate ||(startDate.getDate() === endDate.getDate() &&
    startDate.getMonth() === endDate.getMonth());

    const sameMonth = !endDate || (startDate.getMonth() === endDate.getMonth());


    /*const start = formatDate(event.start, { day: "numeric" });
    const end = formatDate(endDate || event.start, { day: "numeric" });*/
    const dateOptions = { day: "numeric", month: "short", year: "numeric" };
    const timeOnlyOptions = {hour:"2-digit", minute:"2-digit", hour12:false};
    const dayMonthOptions = { day: "numeric", month: "short" }//for when a datee range spans over 2 different months or more


    if (event.allDay) {
      return sameDay ?
        formatDate(event.start, dateOptions)
        : sameMonth ?
          `${startDate.getDate()} - ${formatDate(endDate, dateOptions)}`:// same month: 12 - 15 Feb 2026
          `${formatDate(event.start, dayMonthOptions)} - ${formatDate(endDate, dateOptions)}`;//different mnth: 27 Feb - 3 Mar 2026
    }
    else {
      const startTime = formatDate(event.start, timeOnlyOptions);
      const endTime = formatDate(endDate || event.start, timeOnlyOptions);

      if (sameDay) {
        return `${formatDate(event.start, dateOptions)} ${startTime} - ${endTime}`;
      }
      else if (sameMonth) {
        return `${startDate.getDate()} - ${formatDate(endDate, dateOptions)} ${startTime} - ${endTime}`;
      }
      else {
        return `${formatDate(event.start, dayMonthOptions)} - ${formatDate(endDate, dateOptions)} ${startTime} - ${endTime}`;
      }
    }
  }



  /* Selecting a range on the calendar no longer fires a native prompt(): we stash the selection and open
     * the create-closure modal. The drag highlight is kept (selectMirror) so the admin can see exactly
     * what they're about to close while they type the reason. */
  const handleDateClick = (selected) => {
    // Don't let the admin create a closure while the existing ones haven't loaded (or failed to) - they'd
    // be acting blind and could overlap a closure they can't see. `selectable` already blocks the drag in
    // these states; this is the safety net.
    if (loading || loadFailed) return;
    setPendingSelection(selected);
    setClosureReason("");
    setSelectedBarberId(""); // default every new closure to shop-wide
  };

  // Closes the create-closure modal and clears the leftover drag highlight on the grid.
  const closeClosureModal = () => {
    setPendingSelection(null);
    setClosureReason("");
    setSelectedBarberId("");
    calendarRef.current?.getApi().unselect();
  };

  const confirmCreateClosure = () => {
    if (!pendingSelection) return;
    // Reason is optional: fall back to "Closed" so an unlabeled closure is still identifiable in the list.
    const reason = closureReason.trim() || "Closed";
    createDateClosure(pendingSelection, reason, selectedBarberId);
    // Fire-and-close: submitClosure owns the outcome (success toast, 409 conflict modal, or error toast).
    closeClosureModal();
  };

    const buildClosurePayload = (selected, reason, barberId) => {
        const shopClosureData = {
            startDate: selected.allDay ? selected.startStr : selected.startStr.split('T')[0],
            isFullDay: selected.allDay,
            reason: reason,
            // "" (whole shop) → null so the backend treats it as shop-wide; otherwise the chosen barber.
            barberId: barberId ? Number(barberId) : null,
        }
        if (!selected.allDay) {
            shopClosureData.startTime = selected.startStr.split("T")[1].substring(0, 8);
            shopClosureData.endTime = selected.endStr.split("T")[1].substring(0, 8);
            //.substring(0,8) removes the offset. Example : +01:00
        }
        const rawEndDate = selected.allDay ? selected.endStr : selected.endStr.split("T")[0];

        // Subtract 1 day from endDate (FullCalendar end is exclusive)
        let endDate;
        if (selected.allDay) {
            const endDateObj = new Date(rawEndDate);
            endDateObj.setDate(endDateObj.getDate() - 1); // only subtract for allDay
            endDate = endDateObj.toISOString().split("T")[0];
        } else {
            endDate = rawEndDate; // timed events: use as-is
        }

        if (shopClosureData.startDate !== endDate) {
            shopClosureData.endDate = endDate;
        }
        return shopClosureData;
    }

    const appendClosureEvent = (data) => {
        setEvents(prev => [{
            id: data.id,
            title: user?.role === 'ADMIN' && data.barberName
                ? `${data.barberName} - ${data.reason}`
                : data.reason,
            start: data.isFullDay ? data.startDate : `${data.startDate}T${data.startTime}`,
            end: data.isFullDay
                ? data.endDate
                    ? addDays(data.endDate, 1)  // multi-day: make end exclusive
                    : data.startDate             // single day: just use startDate, FullCalendar handles it
                : `${data.endDate ?? data.startDate}T${data.endTime}`,
            allDay: data.isFullDay
        }, ...prev]);
    }

    /* Posts a closure. When it overlaps existing bookings the backend replies 409 with
     * { requiresConfirmation, message, conflicts[] } instead of creating it; we surface those bookings to
     * the admin (who's emailed vs. who needs a call) and re-post with confirmCancelBookings once they OK it.
     * Any other error becomes a toast rather than a silent console.log. */
    const submitClosure = async (payload) => {
        setSubmitting(true);
        try {
            const res = await adminAxios.post(`/api/dates`, payload);
            appendClosureEvent(res.data);
            if (payload.confirmCancelBookings && conflictData) {
                // Remind the admin about the ones the automatic email can't reach.
                const callList = conflictData.conflicts.filter(c => !c.willBeEmailed);
                showToast(
                    "Closure created",
                    callList.length > 0
                        ? `${conflictData.conflicts.length} booking(s) cancelled. ${callList.length} customer(s) with no email are in Needs Review for you to phone.`
                        : `${conflictData.conflicts.length} booking(s) cancelled and those customers emailed.`
                );
            }
            setConflictData(null);
        }
        catch (err) {
            if (err.response?.status === 409 && err.response.data?.requiresConfirmation) {
                setConflictData({ ...err.response.data, closureData: payload });
            } else {
                showToast("Couldn't create closure", getErrorMessage(err));
            }
        }
        finally {
            setSubmitting(false);
        }
    }

    const createDateClosure = (selected, reason, barberId) => submitClosure(buildClosurePayload(selected, reason, barberId));

    const confirmClosureWithCancellations = () => {
        if (!conflictData) return;
        submitClosure({ ...conflictData.closureData, confirmCancelBookings: true });
    }

    const handleClosureDelete = async (eventId) => {
        try {
            const shopClosureToDelete = await adminAxios.patch(`/api/dates/delete/${eventId}`);
            console.log(shopClosureToDelete?.data?.message);
            setEvents(prev => prev.filter(c => c.id != eventId));
            setDeleteSelectedEvent(null);
        }
        catch (err) {
            console.log(err);
            // Leave the modal open so the admin can retry; tell them it failed rather than silently no-op.
            showToast("Couldn't delete event", getErrorMessage(err));
        }
    }



    // 3. Replace your entire return with this:
    return (
        <Box id="calendar-page">
            {/* Header */}
            <div className="page-header">
                <div className="page-header-text">
                    <h1 className="page-title">Calendar</h1>
                    <p className="page-subtitle">Interactive Calendar Page</p>
                </div>
            </div>

            {/* Closures failed to load: creating one now would be blind, so creation is disabled (see
                `selectable`) until a successful retry. The grid stays viewable in the meantime. */}
            {loadFailed && (
                <div className="calendar-error-banner">
                    <span className="calendar-error-banner__text">
                        <AlertTriangle size={16} />
                        Couldn't load your closures. Creating new closures is disabled until this loads, so
                        you don't add one that overlaps an existing closure you can't currently see.
                    </span>
                    <button type="button" className="calendar-error-banner__retry" onClick={reFetch}>
                        <RotateCw size={14} /> Retry
                    </button>
                </div>
            )}

            <Box display="flex" justifyContent="space-between" className="calendar-layout">

                {/* ── MOBILE: Burger toggle button ── */}
                <button
                    className="calendar-sidebar-toggle"
                    onClick={() => setSidebarOpen(true)}
                >
                    <Menu size={18} />
                    <span>Show Events</span>
                    {events?.length > 0 && (
                        <span className="events-badge">{events.length}</span>
                    )}
                </button>

                {/* ── DESKTOP: Sidebar (hidden on mobile) ── */}
                <Box
                    flex="1 1 20%"
                    backgroundColor="var(--primary-400)"
                    p="15px"
                    borderRadius="4px"
                    className="calendar-sidebar-desktop"
                >
                    <Typography sx={{ color: "var(--grey-100)" }} variant="h5">
                        Events
                    </Typography>
                    <List>
                        {events?.length === 0 && (
                            <Typography sx={{ color: "var(--grey-100)", opacity: 0.7, mt: 1 }}>
                                No upcoming events
                            </Typography>
                        )}
                        {events?.map((event) => (
                            <ListItem
                                key={event.id}
                                onClick={() => goToEventOnCalendar(event)}
                                sx={{
                                    backgroundColor: "#3788d8",
                                    color: "#fff",
                                    margin: "10px 0",
                                    borderRadius: "2px",
                                    cursor: "pointer",
                                    "&:hover": { backgroundColor: "#2c6cb0" },
                                }}
                            >
                                <ListItemText
                                    primary={event.title}
                                    secondary={
                                        <Typography>{formatEventDate(event)}</Typography>
                                    }
                                />
                            </ListItem>
                        ))}
                    </List>
                </Box>

                {/* ── CALENDAR ── */}
                <Box flex="1 1 100%" ml="15px" className="calendar-main">
                    <FullCalendar
                        ref={calendarRef}
                        height="75vh"
                        plugins={[dayGridPlugin, timeGridPlugin, interactionPlugin, listPlugin]}
                        headerToolbar={{
                            left: "prev,next today",
                            center: "title",
                            right: "dayGridMonth,timeGridWeek,timeGridDay,listMonth",
                        }}
                        initialView="dayGridMonth"
                        editable={true}
                        selectable={!loading && !loadFailed}
                        selectMirror={true}
                        dayMaxEvents={true}
                        select={handleDateClick}
                        eventClick={(selected) => setDeleteSelectedEvent(selected.event)}
                        events={events}
                        selectConstraint={{
                            start: currentView === "dayGridMonth" ? startOfDay(now) : now,
                        }}
                        datesSet={(arg) => setCurrentView(arg.view.type)}
                        nowIndicator={true}
                        dayCellClassNames={(arg) =>
                            arg.date < startOfDay(now) ? ["fc-day-past-custom"] : []
                        }
                        eventTimeFormat={{
                            hour: "2-digit",
                            minute: "2-digit",
                            hour12: false,
                        }}
                    />

                    <style>{`
          .fc-day-past-custom {
            background-color: transparent !important;
          }
          .fc-day-past-custom .fc-daygrid-day-number {
            opacity: 0.7;
          }
          .fc-timegrid-col.fc-day-past-custom {
            background-color: var(--muted-fg) !important;
            opacity: 1 !important;
          }
          .fc-timegrid-col.fc-day-today {
            position: relative;
          }
          .fc-timegrid-col.fc-day-today::before {
            content: '';
            position: absolute;
            top: 0; left: 0; right: 0;
            height: ${pastHeightPercent}%;
            background: var(--muted-fg);
            pointer-events: none;
            z-index: 2;
          }
        `}</style>
                </Box>
            </Box>

            {/* ── MOBILE: Bottom Drawer ── */}
            {sidebarOpen && (
                <div
                    className="events-drawer-overlay"
                    onClick={() => setSidebarOpen(false)}
                >
                    <div
                        className="events-drawer"
                        onClick={(e) => e.stopPropagation()}
                    >
                        {/* Drag handle */}
                        <div className="drawer-handle" />

                        <div className="drawer-header">
                            <Typography variant="h5" sx={{ color: "var(--grey-100)" }}>
                                Events
                            </Typography>
                            <button
                                className="drawer-close-btn"
                                onClick={() => setSidebarOpen(false)}
                            >
                                <X size={20} />
                            </button>
                        </div>

                        <div className="drawer-body">
                            {events?.length === 0 ? (
                                <p className="drawer-empty">No upcoming events</p>
                            ) : (
                                <List>
                                    {events?.map((event) => (
                                        <ListItem
                                            key={event.id}
                                            onClick={() => goToEventOnCalendar(event)}
                                            sx={{
                                                backgroundColor: "#3788d8",
                                                color: "#fff",
                                                margin: "10px 0",
                                                borderRadius: "2px",
                                                cursor: "pointer",
                                                "&:hover": { backgroundColor: "#2c6cb0" },
                                            }}
                                        >
                                            <ListItemText
                                                primary={event.title}
                                                secondary={
                                                    <Typography>{formatEventDate(event)}</Typography>
                                                }
                                            />
                                        </ListItem>
                                    ))}
                                </List>
                            )}
                        </div>
                    </div>
                </div>
            )}

            {/* ── Closure Conflict Modal ── */}
            {conflictData && (
                <div
                    className="modal-overlay"
                    onClick={() => !submitting && setConflictData(null)}
                >
                    <div
                        className="modal-content modal-content--wide"
                        onClick={(e) => e.stopPropagation()}
                    >
                        <div className="modal-header">
                            <h2>Bookings affected by this closure</h2>
                            <button
                                className="modal-close"
                                onClick={() => !submitting && setConflictData(null)}
                            >
                                <X size={20} />
                            </button>
                        </div>
                        <div className="modal-body">
                            <p className="closure-conflict-message">{conflictData.message}</p>

                            <ul className="closure-conflict-list">
                                {conflictData.conflicts.map((c) => (
                                    <li key={c.id} className="closure-conflict-item">
                                        <div className="closure-conflict-main">
                                            <span className="closure-conflict-when">
                                                {c.date} · {c.time}
                                            </span>
                                            {c.customer && (
                                                <span className="closure-conflict-name">{c.customer}</span>
                                            )}
                                        </div>
                                        {c.willBeEmailed ? (
                                            <span className="closure-conflict-badge emailed">
                                                <Mail size={14} />
                                                Will be emailed{c.email ? ` · ${c.email}` : ""}
                                            </span>
                                        ) : (
                                            <span className="closure-conflict-badge call">
                                                <Phone size={14} />
                                                {c.phone
                                                    ? `Flagged for review · call ${formatPhone(c.phone)}`
                                                    : c.email
                                                        ? `No phone on file — email ${c.email}`
                                                        : "No contact on file"}
                                            </span>
                                        )}
                                    </li>
                                ))}
                            </ul>

                            <p className="closure-conflict-note">
                                Confirming cancels and refunds these bookings. Customers with an email are
                                notified automatically. Anyone with no email on file — and anyone whose email
                                fails to send — appears in your <strong>Needs Review</strong> list with their
                                phone number, so you can call them by hand.
                            </p>
                        </div>
                        <div className="modal-footer">
                            <button
                                className="btn-secondary"
                                disabled={submitting}
                                onClick={() => setConflictData(null)}
                            >
                                Keep bookings
                            </button>
                            <button
                                className="btn-primary"
                                style={{ background: "#e74c3c" }}
                                disabled={submitting}
                                onClick={confirmClosureWithCancellations}
                            >
                                {submitting
                                    ? "Cancelling…"
                                    : `Cancel ${conflictData.conflicts.length} booking(s) & close`}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* ── Create Closure Modal (replaces the old window.prompt) ── */}
            {pendingSelection && (
                <div className="modal-overlay" onClick={closeClosureModal}>
                    <div
                        className="modal-content"
                        onClick={(e) => e.stopPropagation()}
                        style={{ maxWidth: 460 }}
                    >
                        <div className="modal-header">
                            <h2>Create closure</h2>
                            <button className="modal-close" onClick={closeClosureModal}>
                                <X size={20} />
                            </button>
                        </div>
                        <div className="modal-body">
                            <div className="closure-create-summary">
                                <span className="closure-create-when">{formatEventDate(pendingSelection)}</span>
                                <span className="closure-create-type">
                                    {pendingSelection.allDay ? "Full-day closure" : "Timed closure"}
                                </span>
                            </div>

                            {/* Admins choose whether this closure hits the whole shop or just one barber.
                                Barbers don't see this — their closures are always scoped to themselves. */}
                            {user?.role === "ADMIN" && (
                                <>
                                    <label className="closure-create-label" htmlFor="closure-barber">
                                        Applies to
                                    </label>
                                    <select
                                        id="closure-barber"
                                        className="closure-create-input"
                                        value={selectedBarberId}
                                        onChange={(e) => setSelectedBarberId(e.target.value)}
                                    >
                                        <option value="">Whole shop</option>
                                        {activeBarbers.map((b) => (
                                            <option key={b.id} value={b.id}>
                                                {b.firstName} {b.lastName}
                                            </option>
                                        ))}
                                    </select>
                                </>
                            )}

                            <p className="closure-create-note">
                                {user?.role === "BARBER"
                                    ? "This blocks your availability for the selected time — customers won't be able to book you."
                                    : selectedBarberId
                                        ? (() => {
                                            const b = activeBarbers.find((x) => String(x.id) === String(selectedBarberId));
                                            const name = b ? `${b.firstName} ${b.lastName}` : "this barber";
                                            return `This blocks ${name}'s availability for the selected time — the rest of the shop can still be booked.`;
                                        })()
                                        : "This closes the whole shop for the selected time — no barber can be booked."}
                            </p>

                            <label className="closure-create-label" htmlFor="closure-reason">
                                Reason <span className="closure-create-optional">(optional)</span>
                            </label>
                            <input
                                id="closure-reason"
                                className="closure-create-input"
                                type="text"
                                autoFocus
                                maxLength={100}
                                placeholder="e.g. Public holiday, sick leave — defaults to “Closed”"
                                value={closureReason}
                                onChange={(e) => setClosureReason(e.target.value)}
                                onKeyDown={(e) => { if (e.key === "Enter") confirmCreateClosure(); }}
                            />
                        </div>
                        <div className="modal-footer">
                            <button className="btn-secondary" onClick={closeClosureModal}>
                                Cancel
                            </button>
                            <button
                                className="btn-primary"
                                onClick={confirmCreateClosure}
                            >
                                Create closure
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* ── Delete Event Modal ── */}
            {deleteSelectedEvent && (
                <div
                    className="modal-overlay"
                    onClick={() => setDeleteSelectedEvent(null)}
                >
                    <div
                        className="modal-content"
                        onClick={(e) => e.stopPropagation()}
                        style={{ maxWidth: 420 }}
                    >
                        <div className="modal-header">
                            <h2>Delete Event</h2>
                            <button
                                className="modal-close"
                                onClick={() => setDeleteSelectedEvent(null)}
                            >
                                <X size={20} />
                            </button>
                        </div>
                        <div className="modal-body">
                            <p style={{ color: "var(--muted-fg)", lineHeight: 1.6 }}>
                                Are you sure you want to delete{" "}
                                <strong>{deleteSelectedEvent?.title}</strong>?
                            </p>
                        </div>
                        <div className="modal-footer">
                            <button
                                className="btn-secondary"
                                onClick={() => setDeleteSelectedEvent(null)}
                            >
                                Cancel
                            </button>
                            <button
                                className="btn-primary"
                                style={{ background: "#e74c3c" }}
                                onClick={() => handleClosureDelete(deleteSelectedEvent?.id)}
                            >
                                <Trash2 size={16} />
                            </button>
                        </div>
                    </div>
                </div>
            )}
        </Box>
    );
};
export default AdminCalendar;