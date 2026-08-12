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
import { Trash2, X, Menu, Phone, AlertTriangle, RotateCw } from "lucide-react";
import { AuthContext } from "../../../Context/AuthContext.jsx";
import { ToastContext } from "../../../Context/ToastContext.jsx";
import { adminAxios } from "../../../Hooks/AxiosInterceptor";
import { getErrorMessage } from "../../../utils/errorMessage.js";
import { formatPhone } from "../../../utils/phone.js";

/* Shop-wide closures show up on a barber's calendar alongside his own time off, and the two mean very
 * different things to him - "you're off" vs "the shop is shut around you" - so they don't share a colour.
 * The admin's own view keeps a single colour: it already prefixes every event with the barber's name, and
 * shop-wide ones are the ones with no name. */
const OWN_CLOSURE_COLOUR = "#3788d8";
const SHOP_WIDE_COLOUR = "#b06a1f";

const AdminCalendar = () => {
    const { user } = useContext(AuthContext);
    const { showToast } = useContext(ToastContext);
    /* Creating and removing closures is the manager's job. A barber sees the calendar to know when he
     * isn't working and nothing more, so every mutating control below is gated on this and the backend
     * refuses the writes outright (ADMIN-only on POST /api/dates and PATCH /api/dates/delete). */
    const canManageClosures = user?.role === "ADMIN";
    const closuresUrl = user?.role === 'BARBER'
        ? `/api/dates/barber/${user?.barberId}/closures`
        : '/api/dates/admin/closures';
    const { data, loading, error, reFetch } = useFetch(closuresUrl, true);
    /* Admins can scope a closure to one barber; barbers only ever close their own time, so they don't
     * need (or get) this list. `/api/barbers/admin` includes deactivated barbers, so filter to active -
     * a deactivated barber takes no bookings, so closing their time would be meaningless. */
    const { data: barbersData } = useFetch(user?.role === "ADMIN" ? "/api/barbers/admin" : null, true);
    const activeBarbers = (barbersData ?? []).filter(b => b.isActive);

    /* Drives snapDuration below, so a part-day closure can only be drawn on the same boundaries the booking
     * picker offers. GET /api/Settings is open to ADMIN and BARBER, which is both roles that reach this page.
     *
     * Falls back to 30 - which is what FullCalendar defaults to anyway - so a failed or in-flight settings
     * load leaves the grid behaving exactly as it did before rather than at some finer accidental value. */
    const { data: settingsData } = useFetch("/api/Settings", true);
    const slotStepMin = settingsData?.slotStepMin ?? 30;
    // "" = whole shop (BarberId null); otherwise the selected barber's id.
    const [selectedBarberId, setSelectedBarberId] = useState("");
    const [deleteSelectedEvent, setDeleteSelectedEvent] = useState(null);
    // Bookings a just-deleted closure was holding up - announced, never cleared for them (see below).
    const [reopened, setReopened] = useState(null);
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
        setEvents(data.map(closure => {
            /* Only the barber's view separates the two. The admin already tells them apart by the
               "Name - reason" prefix (shop-wide ones are the ones with no name) and gets one colour, so
               this is gated on the role rather than on closure.isShopWide alone - the admin endpoint
               happens not to populate that flag today, and this shouldn't quietly start recolouring the
               admin's calendar if it ever does. */
            const shopWide = user?.role !== "ADMIN" && closure.isShopWide;
            return {
            id: closure.id,
            /* A barber's list mixes his own time off with shop-wide closures, so those get a "Shop closed"
               prefix as well as their own colour - the Events sidebar renders titles only, and without the
               prefix a public holiday would read there as though it were his personal day off. */
            title: user?.role === "ADMIN"
                ? (closure.barberName ? `${closure.barberName} - ${closure.reason}` : closure.reason)
                : (shopWide ? `Shop closed - ${closure.reason}` : closure.reason),
            start: closure.isFullDay
                ? closure.startDate
                : `${closure.startDate}T${closure.startTime}`,
            end: closure.isFullDay
                ? closure.endDate
                    ? addDays(closure.endDate, 1)  // multi-day: make end exclusive
                    : closure.startDate             // single day: just use startDate, FullCalendar handles it
                : `${closure.endDate ?? closure.startDate}T${closure.endTime}`,
            allDay: closure.isFullDay,
            backgroundColor: shopWide ? SHOP_WIDE_COLOUR : OWN_CLOSURE_COLOUR,
            borderColor: shopWide ? SHOP_WIDE_COLOUR : OWN_CLOSURE_COLOUR,
            };
        }));
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
    // these states; this is the safety net. Barbers can't create closures at all, and `selectable` blocks
    // their drag too - same belt-and-braces.
    if (!canManageClosures || loading || loadFailed) return;
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
            allDay: data.isFullDay,
            /* Must be set here as well as in the load mapping above: the Events list draws its background
               from the event, so a just-created closure would otherwise sit there uncoloured until the next
               reload repopulated it. Only an admin reaches this path, and the admin's calendar is one
               colour, so it isn't conditional. */
            backgroundColor: OWN_CLOSURE_COLOUR,
            borderColor: OWN_CLOSURE_COLOUR,
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
                /* Confirmed bookings aren't cancelled any more - they stay live and go into the worklist, so
                   the toast has to say that plainly. Telling the admin "cancelled and emailed" while the
                   appointments are still on, and the customers still expecting them, would be the worst kind
                   of wrong: they'd stop looking. Only the checkouts are actually gone. */
                const confirmedCount = conflictData.conflicts.filter(c => c.status !== "PENDING").length;
                const pendingCount = conflictData.conflicts.length - confirmedCount;
                /* "info", not the default. showToast falls back to the error type when none is given, so
                   this success - the closure WAS created - was painting red, reading as a failure. Blue
                   rather than green because it isn't purely good news: there's a worklist to go and clear.
                   Matches the deactivate-with-conflicts toast in TeamMembers, which is the same situation. */
                showToast(
                    "Closure created",
                    [
                        confirmedCount > 0
                            ? `${confirmedCount} confirmed booking(s) are still on and flagged in Needs Review — reassign, move or cancel them.`
                            : null,
                        pendingCount > 0 ? `${pendingCount} booking(s) still at checkout were cancelled.` : null,
                    ].filter(Boolean).join(" "),
                    "info"
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
            /* Bookings this closure was holding up. Nothing else shuts their slot now, so their worklist
               notes describe a closure that no longer exists - the admin is shown which ones to go and
               clear, the same courtesy a widened schedule and a reactivated barber already extend. */
            if (shopClosureToDelete?.data?.noLongerClosed?.length)
                setReopened(shopClosureToDelete.data.noLongerClosed);
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
                    <p className="page-subtitle">
                        {canManageClosures
                            ? "Interactive Calendar Page"
                            : "Your time off and the days the shop is closed. Your manager sets these — ask them to add or remove one."}
                    </p>
                </div>
            </div>

            {/* Closures failed to load: creating one now would be blind, so creation is disabled (see
                `selectable`) until a successful retry. The grid stays viewable in the meantime. */}
            {loadFailed && (
                <div className="calendar-error-banner">
                    <span className="calendar-error-banner__text">
                        <AlertTriangle size={16} />
                        {canManageClosures
                            ? `Couldn't load your closures. Creating new closures is disabled until this loads,
                               so you don't add one that overlaps an existing closure you can't currently see.`
                            : `Couldn't load your time off. The calendar below may be missing days you're not
                               working, so check with your manager before relying on it.`}
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
                                // Driven off the event so a shop-wide closure reads the same here as it
                                // does on the grid; brightness() gives it a hover without a second colour.
                                sx={{
                                    backgroundColor: event.backgroundColor,
                                    color: "#fff",
                                    margin: "10px 0",
                                    borderRadius: "2px",
                                    cursor: "pointer",
                                    "&:hover": { filter: "brightness(0.85)" },
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
                        /* The admin's tools for changing closures are dragging out a new range and clicking
                           one to delete it. For a barber the calendar is a read-out, so both are off - the
                           event's title and dates are already spelled out in the Events list beside the
                           grid, which is why clicking one simply does nothing rather than opening a detail
                           popup that would just repeat it.

                           editable is FALSE for everyone, including the admin, because there is nothing
                           behind it: DatesController exposes create (POST), two reads and delete (PATCH
                           delete/{id}) and no update of any kind, and this calendar wires no eventDrop or
                           eventResize. With it on, an admin could drag a closure, watch it land somewhere
                           new, and have it silently snap back on the next load - the change never left the
                           browser. A closure that cannot be moved is better than one that looks moved and
                           isn't. Turn this back on only together with an update endpoint AND the handlers
                           that call it. */
                        editable={false}
                        selectable={canManageClosures && !loading && !loadFailed}
                        /* A part-day closure must land on the boundaries customers actually book on. Left
                           unset, FullCalendar inherits snapDuration from slotDuration and snaps to 30
                           minutes - which matched the seeded slot step by coincidence, not by wiring, so
                           setting "Time between slots" to anything else left the admin unable to draw a
                           closure that lines up with the grid the picker offers.

                           Misalignment costs more than it looks. Both overlap tests are half-open (backend
                           StartTime < bookingEnd && EndTime > bookingStart, and isTimeSlotClosed on the
                           front end), so a slot dies if it touches ANY closed minute: a 13:15-14:00 closure
                           against 30-minute slots removes 13:00-14:00, an hour, while the calendar draws a
                           block starting at 13:15. Snapping costs no expressiveness in exchange - the same
                           slots die either way, because the effect was already quantised.

                           snapDuration and NOT slotDuration on purpose: slotDuration is the row height, and
                           at a 5-minute step it would render 288 rows a day (FullCalendar's own docs warn
                           small slots make the calendar very tall). Snapping finer than the visible rows is
                           the normal configuration for exactly this reason. */
                        snapDuration={{ minutes: slotStepMin }}
                        selectMirror={true}
                        dayMaxEvents={true}
                        select={canManageClosures ? handleDateClick : undefined}
                        eventClick={canManageClosures ? (selected) => setDeleteSelectedEvent(selected.event) : undefined}
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
                        /* Month view shows a part-day closure's start only by default (displayEventEnd is
                           false for dayGridMonth), so "09:00 Dentist" left the admin unable to tell a
                           15-minute closure from one that swallows the afternoon without opening the day.
                           Showing the range - "09:00 - 09:45 Dentist" - answers it from the month grid.

                           Scoped to this view deliberately: the timeGrid views already draw the closure as
                           a block whose height IS its length, and listMonth prints the range already, so
                           setting it globally would only add noise where the answer is already on screen.

                           Full-day closures are unaffected - they are allDay, which suppresses the time
                           display entirely, so they keep reading as just their reason. */
                        views={{ dayGridMonth: { displayEventEnd: true } }}
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
                                            // Same as the desktop list above: colour comes from the event
                                            // so shop-wide closures stay distinguishable in the drawer too.
                                            sx={{
                                                backgroundColor: event.backgroundColor,
                                                color: "#fff",
                                                margin: "10px 0",
                                                borderRadius: "2px",
                                                cursor: "pointer",
                                                "&:hover": { filter: "brightness(0.85)" },
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
                                        {/* The "will be emailed" branch this used to have was unreachable: the
                                            backend hardcodes WillBeEmailed = false (DatesController), because
                                            nothing is sent on confirm any more. It only ever promised an email
                                            that was never coming, so it's gone. What's left is how to REACH
                                            this customer when the admin gets to them. */}
                                        <span className="closure-conflict-badge call">
                                            <Phone size={14} />
                                            {c.phone
                                                ? `Flagged for review · call ${formatPhone(c.phone)}`
                                                : c.email
                                                    ? `No phone on file — email ${c.email}`
                                                    : "No contact on file"}
                                        </span>
                                    </li>
                                ))}
                            </ul>

                            {/* Rewritten: this described the OLD behaviour, where confirming cancelled and
                                refunded the confirmed bookings and emailed the customers. It doesn't any
                                more - they stay live and go to the worklist, and nobody is emailed. Saying
                                customers had been notified while their appointments were still on was the
                                worst way to be wrong: the admin would stop chasing them. */}
                            <p className="closure-conflict-note">
                                Confirming creates the closure. Confirmed bookings are <strong>not</strong> cancelled
                                and <strong>nobody is emailed</strong> — they stay live and appear in your{" "}
                                <strong>Needs Review</strong> list, with a phone number where there's no email on
                                file, so you can reassign, move or cancel each one by hand. The customer is told
                                nothing until you act. Only bookings still at checkout are cancelled automatically.
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
                            {/* No longer red or destructive-sounding: confirming closes the shop and flags the
                                confirmed bookings, it doesn't cancel them. */}
                            <button
                                className="btn-primary"
                                disabled={submitting}
                                onClick={confirmClosureWithCancellations}
                            >
                                {submitting ? "Closing…" : "Close anyway & flag the bookings"}
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
                            {/* WHICH closure this is, not just what it's called. Reasons repeat - a shop
                                closes for "Public holiday" several times a year, and the month grid can
                                only show a truncated title anyway - so a name alone left the admin
                                confirming a deletion they couldn't verify. formatEventDate rather than
                                formatting here, so this and the Events list beside the calendar can never
                                describe the same closure differently; it already prints a part-day
                                closure's start and end, a multi-day range with the exclusive end wound
                                back a day, and a plain date for a single full day. */}
                            <p className="closure-delete-when">
                                {formatEventDate(deleteSelectedEvent)}
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

            {/* The mirror of the conflict modal: that one warns which bookings a closure holds up, this one
                says which ones it stopped holding up. */}
            {reopened && (
                <div className="modal-overlay" onClick={() => setReopened(null)}>
                    <div className="cal-reopened-modal" onClick={(e) => e.stopPropagation()}>
                        <h2>These bookings are back on</h2>
                        <p>
                            {reopened.length === 1 ? "This booking was" : "These bookings were"} flagged for review
                            because the shop was closed for {reopened.length === 1 ? "its" : "their"} slot, and the
                            event you just deleted was what closed it. Nothing has been cleared for you — open{" "}
                            <strong>Needs Review</strong> and read each note. If the closure was the only reason it
                            was flagged, mark it as reviewed. If the note mentions anything else, such as a refund to
                            sort out or a customer to phone, deal with that first.
                        </p>
                        <ul className="cal-reopened-list">
                            {reopened.map((b) => (
                                <li key={b.id}>
                                    <span className="cal-reopened-when">#{b.id} · {b.date} · {b.time}</span>
                                    <span className="cal-reopened-who">
                                        {b.customer || "Customer"}{b.phone ? ` · ${formatPhone(b.phone)}` : b.email ? ` · ${b.email}` : ""}
                                    </span>
                                    {b.stillBlockedBy && (
                                        <span className="cal-reopened-blocked">Still blocked: {b.stillBlockedBy}</span>
                                    )}
                                </li>
                            ))}
                        </ul>
                        <div className="cal-reopened-actions">
                            <button className="btn-primary" onClick={() => setReopened(null)}>Got it</button>
                        </div>
                    </div>
                </div>
            )}
        </Box>
    );
};
export default AdminCalendar;