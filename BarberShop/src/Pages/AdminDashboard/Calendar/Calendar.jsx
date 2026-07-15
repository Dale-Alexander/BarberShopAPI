import { useState, useEffect, useContext } from "react";
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
import { Trash2, X, Menu, Mail, Phone } from "lucide-react";
import { AuthContext } from "../../../Context/AuthContext.jsx";
import { ToastContext } from "../../../Context/ToastContext.jsx";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";

const AdminCalendar = () => {
    const { user } = useContext(AuthContext);
    const { showToast } = useContext(ToastContext);
    const closuresUrl = user?.role === 'BARBER'
        ? `/api/dates/barber/${user?.id}/closures`
        : '/api/dates/admin/closures';
    const { data, loading } = useFetch(closuresUrl, true);
    const [deleteSelectedEvent, setDeleteSelectedEvent] = useState(null);
    const [events, setEvents] = useState([]);
    const [sidebarOpen, setSidebarOpen] = useState(false);
    /* Set when a closure POST comes back 409 because it overlaps existing bookings: holds the backend's
     * { message, conflicts } plus the original payload (closureData) so the admin can review who'd be
     * cancelled and re-submit with confirmCancelBookings. submitting guards the confirm button. */
    const [conflictData, setConflictData] = useState(null);
    const [submitting, setSubmitting] = useState(false);
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
          `${formatDate(event.start, dayMonthOptions)} - ${endDate.getDate()} ${endDate.getFullYear()}`:// same month: 12 - 15 Feb
          `${formatDate(event.start, dayMonthOptions)} - ${formatDate(endDate, dateOptions)}`;//different mnth: 27 feb - 3 Mar 2024
    }
    else {
      const startTime = formatDate(event.start, timeOnlyOptions);
      const endTime = formatDate(endDate || event.start, timeOnlyOptions);

      if (sameDay) {
        return `${formatDate(event.start, dateOptions)} ${startTime} - ${endTime}`;
      }
      else if (sameMonth) {
        return `${formatDate(event.start, dayMonthOptions)} - ${endDate.getDate()} ${endDate.getFullYear()} ${startTime} - ${endTime}`;
      }
      else {
        return `${formatDate(event.start, dayMonthOptions)} - ${formatDate(endDate, dateOptions)} ${startTime} - ${endTime}`;
      }
    }
  }



  const handleDateClick = (selected) => {
    const title = prompt("Please enter a new title for your event");
    const calendarAPI = selected.view.calendar;/* gets a reference
        to the fullCalendar API instance through the "selected" object. This gives
        you access to calendar methods like addEvent, unselect etc */
    calendarAPI.unselect();/* Clears the visual highlight/selection on the calendar
        immediately after the user selects a date. Just cleans up the UI */
      if (title) {
          selected.title = title;
          createDateClosure(selected);

      }
    }

    const buildClosurePayload = (selected) => {
        const shopClosureData = {
            startDate: selected.allDay ? selected.startStr : selected.startStr.split('T')[0],
            isFullDay: selected.allDay,
            reason: selected.title,
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
                        ? `${conflictData.conflicts.length} booking(s) cancelled. Please phone the ${callList.length} customer(s) with no email on file.`
                        : `${conflictData.conflicts.length} booking(s) cancelled and those customers emailed.`
                );
            }
            setConflictData(null);
        }
        catch (err) {
            if (err.response?.status === 409 && err.response.data?.requiresConfirmation) {
                setConflictData({ ...err.response.data, closureData: payload });
            } else {
                showToast("Couldn't create closure", err.response?.data?.message || "An unexpected error occurred. Please try again.");
            }
        }
        finally {
            setSubmitting(false);
        }
    }

    const createDateClosure = (selected) => submitClosure(buildClosurePayload(selected));

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
        }
    }



    // 3. Replace your entire return with this:
    return (
        <Box id="calendar-page" m="20px">
            {/* Header */}
            <div className="calendar-header-title-container">
                <h2 className="calendar-header-title">Calendar</h2>
                <h5 className="calendar-header-subtitle">Interactive Calendar Page</h5>
            </div>

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
                        {events?.map((event) => (
                            <ListItem
                                key={event.id}
                                sx={{
                                    backgroundColor: "#3788d8",
                                    color: "#fff",
                                    margin: "10px 0",
                                    borderRadius: "2px",
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
                        height="75vh"
                        plugins={[dayGridPlugin, timeGridPlugin, interactionPlugin, listPlugin]}
                        headerToolbar={{
                            left: "prev,next today",
                            center: "title",
                            right: "dayGridMonth,timeGridWeek,timeGridDay,listMonth",
                        }}
                        initialView="dayGridMonth"
                        editable={true}
                        selectable={true}
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
                                            sx={{
                                                backgroundColor: "#3788d8",
                                                color: "#fff",
                                                margin: "10px 0",
                                                borderRadius: "2px",
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
                                                    ? `Call: ${c.phone}`
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
                                notified automatically; if an email fails to send, that booking appears in your{" "}
                                <strong>Needs Review</strong> list so you can phone them by hand.
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