import { useState, useMemo, useEffect, useContext, useRef, useLayoutEffect } from "react";
import UserFormModal from "./UserFormModal/UserFormModal.jsx";
import { resolveBarberImage, handleBarberImageError } from "../../utils/barberImage.js";
import { useNavigate, useParams, useSearchParams, useLocation } from "react-router-dom";
import { motion, AnimatePresence } from "framer-motion";
import axios from "axios";
import {
    format,
    startOfMonth,
    endOfMonth,
    startOfWeek,
    endOfWeek,
    addDays,
    addMonths,
    subMonths,
    isSameDay,
    isSameMonth,
    isBefore,
    isAfter,
    startOfDay,
    parse,
    addMinutes
} from "date-fns";
import { ChevronLeft, ChevronRight, SquarePen, UserRound, Scissors, ArrowRight, Calendar } from "lucide-react";
import useFetch from "../../Hooks/useFetch";
import LoadingSpinner from "../../Components/LoadingSpinner/LoadingSpinner";
import ErrorState from "../../Components/ErrorState/ErrorState";
import "./BarberDateAndTime.css";
import { adminAxios } from "../../Hooks/AxiosInterceptor";
import { AuthContext } from "../../Context/AuthContext.jsx";
import { ToastContext } from "../../Context/ToastContext.jsx";
import Navlinks from "../../Components/NavLinks/Navlinks";
import { BookingDetailsContext } from "../../Context/BookingDetailsContext.jsx";
import { getErrorMessage } from "../../utils/errorMessage.js";

/* Fallback slot granularity, used only until barbers-with-bookings answers with the configured
 * ShopSettings.SlotStepMin. There is no fixed slot list: slots are generated per (barber, date) from
 * each barber's effective-dated schedule for a customer, and from the shop's opening hours for staff.
 *
 * Purely how far apart the OFFERED start times are - no backend rule requires a booking to land on this
 * grid, since the server validates an arbitrary DateTime against shifts, closures and overlaps. So a
 * change here can't put the picker out of step with what the server will accept. */
const DEFAULT_SLOT_STEP_MIN = 30;

/* Fallback shop hours, for the same window before the payload arrives. Matches the seeded row. */
const FALLBACK_OPEN_MIN = 9 * 60;
const FALLBACK_CLOSE_MIN = 17 * 60 + 30;

const toMin = (hhmm) => {
    const [h, m] = hhmm.split(":").map(Number);
    return h * 60 + m;
};
const toHHMM = (min) =>
    `${String(Math.floor(min / 60)).padStart(2, "0")}:${String(min % 60).padStart(2, "0")}`;

/* A "yyyy-MM-dd" from the API as LOCAL midnight.
 *
 * Not `new Date(str)`: the spec parses a date-only string as UTC, so east of Greenwich it lands on the
 * right date at the wrong hour - 02:00 local in Malta. Every date the calendar builds is local
 * midnight, so comparing the two made a full-day closure fail to match its own first day, and a
 * single-day closure never matched at all: the shop was shut and the customer could still pick it.
 * Correct in UTC, wrong in the shop's own timezone, which is why it survived so long. */
const parseLocalDay = (s) => {
    const [y, m, d] = String(s).split("-").map(Number);
    return new Date(y, m - 1, d);
};

/* Mirrors the backend ScheduleResolver (VersionForDate + ShiftsForDate): pick the effective-dated
 * schedule version governing `date` (effectiveTo null = current/open-ended, latest effectiveFrom
 * wins), then return that version's shifts for the date's weekday as {startMin,endMin}, sorted.
 * Empty = the barber isn't scheduled that day (closed). Dates compare as ISO "yyyy-MM-dd" strings. */
const shiftsForDate = (schedule, date) => {
    if (!schedule || !date) return [];
    const d = format(date, "yyyy-MM-dd");
    let version = null;
    for (const v of schedule) {
        if (v.effectiveFrom <= d && (v.effectiveTo == null || v.effectiveTo >= d)) {
            if (!version || v.effectiveFrom > version.effectiveFrom) version = v;
        }
    }
    if (!version) return [];
    const dow = date.getDay(); // 0=Sun..6=Sat, matches backend System.DayOfWeek
    return (version.shifts ?? [])
        .filter((s) => s.dayOfWeek === dow)
        .map((s) => ({ startMin: toMin(s.startTime.slice(0, 5)), endMin: toMin(s.endTime.slice(0, 5)) }))
        .sort((a, b) => a.startMin - b.startMin);
};

/* Candidate slot START times (HH:mm) across a span, stepping by the configured granularity and
 * excluding the end itself (a start AT the end can never fit a positive-length booking). */
const stepRange = (startMin, endMin, stepMin) => {
    const out = [];
    for (let m = startMin; m < endMin; m += stepMin) out.push(toHHMM(m));
    return out;
};

/* The barber's candidate slot START times for a date: stepped from each shift's start. Whether a start
 * ACTUALLY fits (duration, grace, split-shift gaps) is slotFitsBarberSchedule below. */
const scheduleSlots = (schedule, date, stepMin) => {
    const out = [];
    for (const sh of shiftsForDate(schedule, date)) out.push(...stepRange(sh.startMin, sh.endMin, stepMin));
    return out;
};

/* The shop's own opening hours for a date, from the seven ShopHours rows the API sends. null = the shop
 * is shut that weekday (or the row is missing, which is treated the same way and fails closed). */
const shopHoursForDate = (shopHours, date) => {
    if (!date) return null;
    const row = (shopHours ?? []).find((h) => h.dayOfWeek === date.getDay());
    if (!row || row.isClosed) return null;
    return { openMin: toMin(row.openTime.slice(0, 5)), closeMin: toMin(row.closeTime.slice(0, 5)) };
};

/* Current time as Malta wall-clock. The slot strings ("09:00") are Malta wall-clock times and the backend
 * validates them against Malta time (ShopClock), so "now" must be Malta's clock too - otherwise a non-Malta
 * browser would grey out the wrong slots (e.g. show a morning slot that Malta has already passed). */
const getMaltaNow = () =>
    new Date(new Date().toLocaleString("en-US", { timeZone: "Europe/Malta" }));

const BarberDateAndTime = () => {
    const [barbers, setBarbers] = useState([]);
    const [shopWideClosures, setShopWideClosures] = useState([]);
    /* Between-booking buffer (minutes) from the backend, so greyed-out slots match what the
     * booking-create overlap check will accept. 0 = disabled (back-to-back allowed). */
    const [bufferMin, setBufferMin] = useState(0);
    /* Minutes a booking may run past closing, from the backend. Late start slots that wouldn't
     * finish by close + this are hidden from customers. 0 = must finish by closing. */
    const [graceMinutesAfterClose, setGraceMinutesAfterClose] = useState(0);
    /* Customer-only lead-time / horizon, from the backend (ShopSettings, delivered via
     * barbers-with-bookings), mirroring ValidateBookingTime: a customer must book at least
     * minAdvanceMinutes ahead and at most maxAdvanceDays out. Defaults match the seed until loaded. */
    const [minAdvanceMinutes, setMinAdvanceMinutes] = useState(90);
    const [maxAdvanceDays, setMaxAdvanceDays] = useState(60);
    /* Spacing of the offered start times, from ShopSettings. */
    const [slotStepMin, setSlotStepMin] = useState(DEFAULT_SLOT_STEP_MIN);
    /* The shop's own opening hours, one row per weekday. These draw the STAFF grid (staff aren't bounded
     * by any barber's hours, so there'd otherwise be nothing to draw) and mark which slots are outside
     * them. Customers never read these: their grid comes from the selected barber's shifts, which the
     * backend already keeps inside these hours. */
    const [shopHours, setShopHours] = useState([]);
    /* Staff-only escape from that grid. Shop hours cap what staff are OFFERED, not what they may book -
     * the shop can open late for a regular, and the whole out-of-hours confirmation exists for it. Off by
     * default because the alternative is a grid of every slot in the day: 48 chips at a 30-minute step and
     * 96 at 15, nearly all of them useless. Never shown to customers, who cannot override anything. */
    const [showExtendedHours, setShowExtendedHours] = useState(false);
    const [calendarMonth, setCalendarMonth] = useState(new Date());
    const { bookingId } = useParams();
    const isEditMode = !!bookingId;
    const [originalTime, setOriginalTime] = useState(null);
    const [originalDate, setOriginalDate] = useState(null);
    const navigate = useNavigate();
    const location = useLocation();
    const { user, loading } = useContext(AuthContext);
    const [searchParams] = useSearchParams();
    const isAdminBooking = searchParams.get("adminBooking") === "true";
    const isAdmin = user?.role === "ADMIN";
    /* Where "back to the list" goes. The bookings tables hand over the URL they were showing (filters and
       all) in history state, because a bare "/admin" drops the query string - and with it the needs-review
       worklist the admin was working through. Landing on the default confirmed view after fixing a flagged
       booking hides the very row they still have to mark reviewed, which is the one thing the
       recentlyEditedBookingId marker below exists to stop them forgetting.
       Survives a refresh without any help from us: React Router keeps location.state in history.state,
       which the browser restores for the entry on reload - so don't "fix" this by mirroring it into
       storage, which would only add a stale copy outliving the visit that set it. The fallback is for
       entries that never had it: creating a booking, or a pasted/bookmarked /datetime/:id URL.
       Nothing needs to clear it either - only recentlyEditedBookingId is passed back to the list, so
       `from` stays on this page's own history entry, where a browser Back still wants it. */
    const returnTo = location.state?.from ?? (isAdmin ? "/admin" : `/admin/team/${user?.barberId}`);
    // Staff (admin OR barber) create bookings from the dashboard; customers use the public flow.
    const isStaffMode = (isAdmin || user?.role === "BARBER") && isAdminBooking;
    /* A barber only ever works on their own chair, creating or editing, so the picker is locked to them
       (the backend enforces it too). Editing used to be exempt - a barber could hand an existing booking
       to a colleague - which contradicted the create rule, where they can't give a colleague new work.
       Putting work on someone else's day is the owner's call, so it's an admin action either way. */
    const lockBarberToSelf = user?.role === "BARBER" && (isStaffMode || isEditMode);
    /* Everyone who isn't staff booking or staff rescheduling. Declared up here rather than beside the
     * lead-time rules it also drives, because the availability fetch below picks its roster from it. */
    const isCustomer = !isStaffMode && !isEditMode;
    const [showModal, setShowModal] = useState(false);
    // Staff-only warn-and-confirm shown when a staff member picks a slot outside the target barber's
    // schedule (they can still book it - see the Next handler). Customers are hard-limited, never warned.
    const [showScheduleWarn, setShowScheduleWarn] = useState(false);
    /* Sticky version of that confirmation, for the staff CREATE path: the booking isn't sent until the
       customer-details modal has been filled in, by which point the confirmation is several clicks back.
       The edit path passes it straight through instead and never reads this. */
    const [outsideHoursConfirmed, setOutsideHoursConfirmed] = useState(false);
    const { showToast } = useContext(ToastContext);
    const [bookingLoading, setBookingLoading] = useState(false);
    const {
        selectedBarberId, setSelectedBarberId,
        selectedDate, setSelectedDate,
        selectedTime, setSelectedTime,
        chosenServiceIds,
        chosenServicesDurationMin,
    } = useContext(BookingDetailsContext);
    /* Admin bookings aren't tied to services, so the admin picks a duration on this page. Seeded
     * from the shop-wide default (ShopSettings) and editable live so the slot picker greys
     * accurately before the details modal.
     * Two states on purpose: `adminDurationInput` is the raw text in the box (can be "" mid-edit),
     * while `adminDurationMin` is the last VALID number and only updates on valid input - so
     * clearing the field to retype doesn't drop the greying back to the default. */
    const [adminDurationMin, setAdminDurationMin] = useState(30);
    const [adminDurationInput, setAdminDurationInput] = useState("30");
    //if the admin forgets to logout and manually set adminBooking to be true
    //then it still could be so that when he makes a booking as a normal user
    //he doesnt get the normal experience. Just validate on backend then.

    /* what this does is it effectively converts
        bookingId to a boolean. If its not falsy("", false, 0, null, undefined), 
        isEditMode will be set to true*/
    /* Fetches when barbers are booked, when they're closed and when the whole shop is closed.
     *
     * Anonymous for customers (the endpoint is public), but staff send credentials and ask for the staff
     * roster: `includeUnbookable` keeps barbers who are closed to NEW customer bookings in the list, since
     * that flag is about customers only. Without it a barber winding down disappeared from their own edit
     * page and couldn't touch the appointments they were still working through. The backend ignores the
     * flag unless the caller really is staff. */
    const { data: barberBookings, loading: barberBookingsloading, error: barberBookingsError, reFetch: reFetchBarbers } = useFetch(
        isCustomer ? `/api/Barbers/barbers-with-bookings` : `/api/Barbers/barbers-with-bookings?includeUnbookable=true`,
        !isCustomer);
    const { data: editBooking, error: editBookingError } = useFetch(bookingId ? `/api/Bookings/admin/${bookingId}` : null, true);
    //admin/{bookingId} is [Authorize(ADMIN,BARBER)], so it always needs credentials (isProtected = true)
    /* Admin-only: pull the default booking duration so the on-page duration control starts at the
     * shop's configured default (admin auth is required, so this uses adminAxios via isProtected). */
    // GET /api/Settings is readable by staff (ADMIN or BARBER), so any staff booking seeds the duration
    // control from the shop default - not just admins. Customers never fetch it (they use service durations).
    const { data: shopSettings } = useFetch(isStaffMode ? `/api/Settings` : null, true);



    //this so when the user picks a barber, date and time but then he selects another barber and turns out that that barber has that slot fully booked, this will detect a change
    //in the barber and see whether that time slot is booked. If so then time will be set to null


    useEffect(() => {
        if (!barberBookings) return;//this if statement is very important. without it you are calling undefined?.barbers which will cause the page to not render
        console.log(barberBookings);
        setBarbers(barberBookings?.barbers);
        setShopWideClosures(barberBookings?.shopClosures);
        setBufferMin(barberBookings?.bufferMin ?? 0);
        setGraceMinutesAfterClose(barberBookings?.graceMinutesAfterClose ?? 0);
        setMinAdvanceMinutes(barberBookings?.minAdvanceBookingMinutes ?? 90);
        setMaxAdvanceDays(barberBookings?.maxAdvanceBookingDays ?? 60);
        setSlotStepMin(barberBookings?.slotStepMin || DEFAULT_SLOT_STEP_MIN);
        setShopHours(barberBookings?.shopHours ?? []);
        if (bookingId && editBooking) {
            const selectedBarber = barberBookings?.barbers.find(b => b?.barberId == editBooking?.barberId);
            setSelectedBarberId(selectedBarber?.barberId ?? null);
            const startDate = new Date(editBooking?.startDateTime);
            setSelectedDate(startOfDay(startDate));
            setSelectedTime(format(startDate, "HH:mm"));
            setOriginalTime(format(startDate, "HH:mm"));
            setOriginalDate(startOfDay(startDate));
            setCalendarMonth(startDate);
        }
    }, [barberBookings, editBooking, bookingId])


    useEffect(() => {
        if (shopSettings?.defaultAdminBookingDurationMin) {
            setAdminDurationMin(shopSettings.defaultAdminBookingDurationMin);
            setAdminDurationInput(String(shopSettings.defaultAdminBookingDurationMin));
        }
    }, [shopSettings]);

    // Entering a fresh booking (not edit): clear any picker state left over in BookingDetailsContext
    // from a previous flow - e.g. the admin just edited a booking, then hit "Create Booking"; those
    // date/time/barber selections must not carry over. Edit mode re-populates from the booking being
    // edited (below), so it's exempt. A barber's own chair is re-applied by the auto-select effect.
    useEffect(() => {
        if (!isEditMode) {
            setSelectedDate(null);
            setSelectedTime(null);
            setSelectedBarberId(null);
        }
        // Run once on mount; the deps are stable for this page instance.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    /* A confirmation is only good for the slot it was given for. Changing the barber, the day or the time
       throws it away, so an "yes, book them late" agreed for Friday 19:00 can't ride along to a different
       pick the admin never looked at a warning for. */
    useEffect(() => {
        setOutsideHoursConfirmed(false);
    }, [selectedBarberId, selectedDate, selectedTime]);

    // Barber staff-create: pre-select their own chair (they can't pick anyone else). Edit mode already
    // seeds the barber from the booking being edited, so this only runs for the create flow.
    useEffect(() => {
        if (lockBarberToSelf && !isEditMode && user?.barberId != null) {
            setSelectedBarberId(user.barberId);
        }
    }, [lockBarberToSelf, isEditMode, user?.barberId]);

    /* Edit mode couldn't load the booking being edited (e.g. it was cancelled, or the id is bad -> 404).
       Without the booking there's nothing to prefill or save against, so tell the staff member and send
       them back to the dashboard. 401 is left to the axios interceptor, which redirects to login. */
    useEffect(() => {
        if (isEditMode && editBookingError && editBookingError.response?.status !== 401) {
            showToast("Couldn't load booking", getErrorMessage(editBookingError, "This booking may no longer exist."));
            navigate(returnTo, { replace: true });
        }
    }, [isEditMode, editBookingError]);

    /* The length of the booking being placed, used to grey slots by the real appointment length.
     * Edit: the existing booking's duration. Admin: the on-page control. Customer: the chosen
     * services' total (falls back to 30 until the Services page populates it). */
    const DEFAULT_SLOT_MIN = 30;
    const slotDurationMin =
        isEditMode ? (editBooking?.durationMin ?? DEFAULT_SLOT_MIN)
        // Uses the last VALID staff duration, so greying stays put (e.g. at 60) while the field is
        // temporarily empty during editing rather than snapping to the default.
        : isStaffMode ? (adminDurationMin || DEFAULT_SLOT_MIN)
        : (chosenServicesDurationMin || DEFAULT_SLOT_MIN);

    /* Admin-only inline validation for the on-page duration box (mirrors the backend 5-240 bound).
     * Shown under the field and used to gate the "Next" button, so a bad value is caught here rather
     * than as a toast after the details modal. Number("") is 0, so an empty box is flagged too. */
    const adminDurationError = isStaffMode
        ? (() => {
            const n = Number(adminDurationInput);
            return (!Number.isInteger(n) || n < 5 || n > 240)
                ? "Enter a whole number of minutes between 5 and 240."
                : null;
        })()
        : null;

    const capitalize = (str) => str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();

    const today = startOfDay(getMaltaNow());

    const maxCustomerDate = isCustomer ? addDays(today, maxAdvanceDays) : null;
    const isDateBeyondHorizon = (day) => !!maxCustomerDate && isAfter(startOfDay(day), maxCustomerDate);

    const handleCancel = () => {
        setShowModal(false);
    }

    const calendarDays = useMemo(() => {
        const monthStart = startOfMonth(calendarMonth);
        const monthEnd = endOfMonth(calendarMonth);
        const weekStart = startOfWeek(monthStart);
        const weekEnd = endOfWeek(monthEnd);
        /* all of the above return date objects. Ex: 2026-03-21T23:59:59.999 */
        const days = [];
        let day = weekStart;
        while (day <= weekEnd) {
            days.push(day);
            day = addDays(day, 1);
        }
        return days;
    }, [calendarMonth]);

    //The below function checks whether the shopwide closure falls on that date
    const isDateClosed = (day) => {
        if (!day) return false;
        if (!selectedBarberId) return false;
        const isWithinClosure = (c) => {
            const start = parseLocalDay(c.startDate);
            const end = c.endDate ? parseLocalDay(c.endDate) : start;
            return c.isFullDay && day >= start && day <= end;
        }
        const shopWide = shopWideClosures.some(isWithinClosure);
        if (selectedBarberId === "All") return shopWide;
        const selectedBarber = barbers.find(b => b.barberId === selectedBarberId);
        const barberSpecific = selectedBarber?.dateClosures.some(isWithinClosure);
        return shopWide || barberSpecific;
    }

    const isDateBooked = (day) => {
        if (!day) return false;
        if (!selectedBarberId) return false;
        return getAvailableSlots(day).length === 0;
    }
    /* The staff time grid for a date: the shop's opening hours for THAT WEEKDAY, or the whole day when
     * extended hours are switched on.
     *
     * This used to scan every barber's every schedule version for the widest start and end anywhere, which
     * was wrong three ways at once: a version effective from next month widened today's grid, Saturday's
     * early opening widened Tuesday's, and the widest hours anyone had were also the furthest staff could
     * ever reach - so in a shop where nobody works past 17:30, an 18:00 favour was simply not on the grid,
     * even though the backend would have taken it with a confirmation. Shop hours answer all three: they're
     * per-weekday, they don't depend on rosters at all, and what staff may exceed is now a deliberate
     * toggle rather than an accident of whose shift happened to run latest.
     *
     * A day the shop is closed gives an empty grid - correct, and reachable via extended hours, which is
     * exactly the Sunday-opening case. */
    const staffGridSlots = (date) => {
        if (showExtendedHours) return stepRange(0, 24 * 60, slotStepMin);
        const hours = shopHoursForDate(shopHours, date);
        // Only while the payload is in flight; a genuinely closed day resolves to null AFTER it arrives,
        // and must stay empty rather than falling back to hours the shop doesn't keep.
        if (!hours) return shopHours.length === 0
            ? stepRange(FALLBACK_OPEN_MIN, FALLBACK_CLOSE_MIN, slotStepMin)
            : [];
        return stepRange(hours.openMin, hours.closeMin, slotStepMin);
    };

    /* The candidate slot START times shown for a date, BEFORE removing booked/closed/past ones:
     * customers get the selected barber's schedule slots (hard-limited to their real hours); staff get
     * the shop-hours grid, with out-of-hours ones marked rather than removed (see isOutsideSchedule). */
    const slotUniverse = (date) => {
        if (!date || !selectedBarberId) return [];
        if (!isCustomer) return staffGridSlots(date);
        if (selectedBarberId === "All") {
            const set = new Set();
            barbers.forEach((b) => scheduleSlots(b.schedule, date, slotStepMin).forEach((s) => set.add(s)));
            return [...set].sort();
        }
        const barber = barbers.find((b) => b.barberId === selectedBarberId);
        return scheduleSlots(barber?.schedule, date, slotStepMin);
    };

    const getAvailableSlots = (date) => {
        /* Removes slots that overlap an existing booking (buffer-expanded on both sides to mirror the
           backend's between-booking gap). The slot universe it filters is schedule-driven for customers /
           the envelope for staff (see slotUniverse). */
        if (!date) return [];
        if (!selectedBarberId) return [];
        const universe = slotUniverse(date);
        if (selectedBarberId === "All") {
            return universe.filter(slot => {//FOR EVERY SLOT
                const slotStart = parse(slot, "HH:mm", date);
                const slotEnd = addMinutes(slotStart, slotDurationMin);

                return barbers.some(b => {//IS THERE ANY BARBER
                    // ?? [] guards the brief window where a barber is selected but `barbers` hasn't
                    // loaded yet (or a booking has no bookings array) - otherwise .some throws on undefined.
                    const bookingsOnDay = b?.bookings?.filter(bk => isSameDay(new Date(bk.startDateTime), date)) ?? [];
                    return !bookingsOnDay.some(bk => {//WHOSE BOOKINGS DO NOT OVERLAP THIS SLOT
                        const bkStart = new Date(bk.startDateTime);
                        const bkBlockStart = addMinutes(bkStart, -bufferMin);
                        const bkEnd = addMinutes(bkStart, bk.durationMin + bufferMin);
                        return slotStart < bkEnd && slotEnd > bkBlockStart;
                    });
                });
            });
        }
        const barber = barbers.find(b => selectedBarberId === b.barberId);
        // ?? [] guards the window where selectedBarberId is set (e.g. a barber auto-selected onto their
        // own chair) but `barbers` hasn't loaded yet, so `barber` is momentarily undefined.
        const barberBookingsOnDay = barber?.bookings?.filter(bk => isSameDay(new Date(bk.startDateTime), date)) ?? [];
        return universe.filter(slot => {
            const slotStart = parse(slot, "HH:mm", date);
            const slotEnd = addMinutes(slotStart, slotDurationMin);

            return !barberBookingsOnDay.some(bk => {
                const bkStart = new Date(bk.startDateTime);
                /* Blocked window expanded by the buffer on both sides so the picker matches the
                 * backend's between-booking gap. */
                const bkBlockStart = addMinutes(bkStart, -bufferMin);
                const bkEnd = addMinutes(bkStart, bk.durationMin + bufferMin);
                return slotStart < bkEnd && slotEnd > bkBlockStart;
            });
        });
    }

    /* Both the slot time and getMaltaNow() are built as browser-local Dates holding Malta wall-clock values,
     * so comparing them is a pure Malta-vs-Malta wall-clock comparison (see getMaltaNow's note). */
    const slotDateTimeOf = (time, date) => {
        const [hours, minutes] = time.split(":").map(Number);
        const slotDateTime = new Date(date);
        slotDateTime.setHours(hours, minutes, 0, 0);
        return slotDateTime;
    };

    const isTimeSlotInPast = (time, date = selectedDate) => {
        if (!date) return false;
        return slotDateTimeOf(time, date) < getMaltaNow();
    };

    /* Customer-only: within the configured lead time. This also covers "in the past" (past is
     * < now < now + lead time), so for customers it fully subsumes isTimeSlotInPast. */
    const isTimeSlotTooSoon = (time, date = selectedDate) => {
        if (!date) return false;
        return slotDateTimeOf(time, date) < addMinutes(getMaltaNow(), minAdvanceMinutes);
    };

    /* Mirrors backend ScheduleResolver.FitsWithinAShift: does [time, time+duration] fit ENTIRELY within
     * one of the barber's shifts for `date`? Grace (minutes past close) applies only to the day's last
     * shift, never a split-shift lunch gap. No shifts that day => false (fail closed). */
    const slotFitsBarberSchedule = (time, date, barber) => {
        const shifts = shiftsForDate(barber?.schedule, date);
        if (shifts.length === 0) return false;
        const startMin = toMin(time);
        const endMin = startMin + slotDurationMin;
        for (let i = 0; i < shifts.length; i++) {
            const isLast = i === shifts.length - 1;
            const allowedEnd = shifts[i].endMin + (isLast ? graceMinutesAfterClose : 0);
            if (startMin >= shifts[i].startMin && endMin <= allowedEnd) return true;
        }
        return false;
    };

    /* Does this slot fall outside the selected barber's scheduled hours for the date? Mirrors the
     * backend's ScheduleResolver, including grace past the day's last shift and the duration being
     * booked. Skipped for "All", where there is no one barber's schedule to measure against. */
    const isTimeSlotOutsideSchedule = (time, date = selectedDate) => {
        if (selectedBarberId === "All") return false;
        const barber = barbers.find((b) => b.barberId === selectedBarberId);
        return !slotFitsBarberSchedule(time, date, barber);
    };

    /* The OTHER boundary: does [time, time+duration] fall outside the shop's own opening hours? Mirrors
     * the backend ShopHoursResolver, grace included - Req 2 makes a shift ending at closing time the norm,
     * so without grace here a non-zero GraceMinutesAfterClose would mark the day's last slot out-of-hours
     * on a shop that has explicitly allowed it to run over.
     *
     * Separate from the barber question on purpose: they're independent, they're overridden independently
     * on the backend, and a staff member needs to see which of the two they're about to step outside.
     * Only reachable with extended hours on - the ordinary staff grid stops at these hours. */
    const isTimeSlotOutsideShopHours = (time, date = selectedDate) => !!shopHoursBreach(time, date);

    /* WHICH side of the shop's hours a slot falls foul of, or null if none.
     *
     * Worth distinguishing because the three cases are not the same news. A 17:15 start in a shop that
     * shuts at 17:30 is INSIDE opening hours - it is the appointment that runs over - and calling that
     * "outside the shop's opening hours" invites the reader to check a clock that says otherwise. The
     * distinction is also actionable: an overrun is what GraceMinutesAfterClose exists to permit, whereas
     * a closed day or an early start is not. */
    const shopHoursBreach = (time, date = selectedDate) => {
        if (!date) return null;
        const hours = shopHoursForDate(shopHours, date);
        // No row = the shop is shut that weekday. Null while the payload is still in flight, when nothing
        // should be marked at all.
        if (!hours) return shopHours.length > 0 ? { kind: "closed" } : null;
        const startMin = toMin(time);
        if (startMin < hours.openMin) return { kind: "before-open", at: toHHMM(hours.openMin) };
        if (startMin + slotDurationMin > hours.closeMin + graceMinutesAfterClose)
            return { kind: "overruns", at: toHHMM(hours.closeMin) };
        return null;
    };

    /* The same question for the selected barber's shifts. "Runs past the end of their shift" and "they
     * aren't working then" read very differently to whoever has to agree to it. */
    const barberHoursBreach = (time, date = selectedDate) => {
        if (selectedBarberId === "All" || !date) return null;
        const barber = barbers.find((b) => b.barberId === selectedBarberId);
        const shifts = shiftsForDate(barber?.schedule, date);
        if (shifts.length === 0) return { kind: "day-off" };
        if (slotFitsBarberSchedule(time, date, barber)) return null;
        const startMin = toMin(time);
        // Started inside a shift but ran past its end - as opposed to landing in a lunch gap or outside
        // the day's shifts entirely.
        const covering = shifts.find((s) => startMin >= s.startMin && startMin < s.endMin);
        return covering
            ? { kind: "overruns", at: toHHMM(covering.endMin) }
            : { kind: "outside" };
    };

    /* Wording for the out-of-hours confirmation. A barber is locked to their own chair, so they are always
     * agreeing to work the slot themselves and get the soft copy; an admin is always committing somebody
     * else and gets the "make sure they've agreed" copy. */
    const isBookingSelf = user?.role === "BARBER";
    const selectedBarberName = barbers.find((b) => b.barberId === selectedBarberId)?.barberName ?? "this barber";

    /* Staff-only: which boundaries does the currently SELECTED slot step outside? Drives the
     * warn-and-confirm on the Next button and which of the three messages it shows.
     *
     * The shop-hours half is asked even when the barber is "All", where there is no one schedule to
     * measure against - the shop's hours don't depend on which barber it is. */
    const selectedSlotOverrides = () => {
        if (isCustomer || !selectedTime || !selectedDate || !selectedBarberId)
            return { schedule: false, shop: false, any: false };
        const schedule = selectedBarberId !== "All" && isTimeSlotOutsideSchedule(selectedTime, selectedDate);
        const shop = isTimeSlotOutsideShopHours(selectedTime, selectedDate);
        return { schedule, shop, any: schedule || shop };
    };
    const selectedSlotOutsideSchedule = () => selectedSlotOverrides().any;

    /* One place that turns a breach into words, so the chip's tooltip and the confirmation modal can never
     * describe the same slot differently. `who` varies because the modal addresses a barber about their
     * own chair ("your shift") and an admin about someone else's ("Alex's shift"), while the tooltip is
     * always third-person. */
    const shopBreachPhrase = (breach, dayName) => {
        if (!breach) return null;
        if (breach.kind === "closed") return `the shop is closed on ${dayName}s`;
        if (breach.kind === "before-open") return `the shop doesn't open until ${breach.at}`;
        return `it would run past closing (${breach.at})`;
    };
    /* `subject` and `possessive` are passed in rather than derived, because the same sentence is said to
     * two different people: a barber about their own chair ("you aren't working", "your shift") and an
     * admin about someone else's ("Alex isn't working", "Alex's shift"). */
    const barberBreachPhrase = (breach, subject, possessive, dayName) => {
        if (!breach) return null;
        if (breach.kind === "day-off") return `${subject} not working on ${dayName}s`;
        if (breach.kind === "overruns") return `it would run past the end of ${possessive} shift (${breach.at})`;
        return `it's outside ${possessive} working hours`;
    };

    /* The booking being edited is still sitting on the barber it belongs to. This gates the exemption
     * that keeps a booking's OWN slot selectable and selected: without that exemption, opening an edit to
     * change something else would grey out and then clear the time the booking already has.
     *
     * Once it's been handed to a different barber, though, that time isn't "its own slot" any more - it's
     * just a time on somebody else's day, and it has to pass the same checks as any other. Non-edit flows
     * have no original slot to protect, so they're trivially true. */
    const isOnOriginalBarber = !isEditMode || (editBooking != null && selectedBarberId === editBooking.barberId);

    /* Whether an edit actually changes the slot or barber - so the warn (and handleEdit's own no-op
     * guard) don't fire on an unchanged "Confirm Edit". */
    const editHasChanges = () => {
        if (!isEditMode || !editBooking || !selectedDate || !selectedTime) return false;
        const orig = new Date(editBooking.startDateTime);
        const dtChanged = format(selectedDate, "yyyy-MM-dd") !== format(orig, "yyyy-MM-dd")
            || format(orig, "HH:mm") !== selectedTime;
        return dtChanged || editBooking.barberId !== selectedBarberId;
    };

    const isTimeSlotClosed = (time, date = selectedDate) => {
        /* partial closures can never be multi-day (DatesController rejects it), so just keep checking
           startDate and ignore endDate because it is irrelevant here */
        if (!date) return false;
        if (!selectedBarberId) return false;
        const dateStr = format(date, "yyyy-MM-dd");//dont change this format because dateOnly stores it in this format
        /* The booking's whole span, not just where it starts. Mirrors the backend's closure overlap test
           (BookingsController create/update, BookingSlotGuard.ClosuresCoveringSlot):
               closure.start < bookingEnd && closure.end > bookingStart
           Testing only the start let a booking that BEGINS before a partial closure and RUNS INTO it show
           as available, and the backend then refused it on submit - a 30-minute booking at 12:45 against a
           13:00-14:00 closure. Rare while slots step every 30 minutes and closures are set on the hour, so
           the start lands on the boundary anyway; routine the moment either of those stops being true.
           No buffer here on purpose: the between-booking gap applies to bookings only, never to closures.
           Comparing in minutes rather than as strings because "12:45" + 30 isn't a string operation - and
           it drops the HH:mm:ss-vs-HH:mm slicing hazard the old comparison had to keep explaining. */
        const startMin = toMin(time);
        const endMin = startMin + slotDurationMin;
        const overlaps = (c) =>
            toMin(c.startTime.slice(0, 5)) < endMin && toMin(c.endTime.slice(0, 5)) > startMin;
        /* The times are nullable in the database, and a partial closure without them can't be reasoned
           about - so skip it rather than throw. The API rejects such a closure on create, but the column
           allows it, and a null here would otherwise take down the whole booking page on .slice(). */
        const partialOnDay = (closures) => (closures ?? [])
            .filter(c => !c.isFullDay && c.startDate === dateStr && c.startTime && c.endTime);

        const shopWide = partialOnDay(shopWideClosures).some(overlaps);
        if (selectedBarberId === "All") return shopWide;
        const selectedBarber = barbers.find(b => b.barberId === selectedBarberId);
        return shopWide || partialOnDay(selectedBarber?.dateClosures).some(overlaps);
    }

    useEffect(() => {
        if (selectedTime && selectedDate) {
            const isFree = getAvailableSlots(selectedDate).includes(selectedTime);
            const isClosed = isTimeSlotClosed(selectedTime);
            const isPast = isTimeSlotInPast(selectedTime);
            /* Switching barber can leave a customer holding a time the new barber doesn't work. Customers
               only: staff may take an out-of-hours slot deliberately, so for them it stays selected and
               simply becomes a pick they'll be asked to confirm - a choice, not a dead end. */
            const outsideForCustomer = isCustomer && isTimeSlotOutsideSchedule(selectedTime);
            if (!isFree || isClosed || isPast || outsideForCustomer) {
                if (!(isOnOriginalBarber && selectedTime === originalTime)) setSelectedTime(null);
            }
        }

        if (selectedDate) {
            if (isDateBooked(selectedDate)) {
                if (!isSameDay(selectedDate, originalDate)) setSelectedDate(null);
            }
        }
        // slotDurationMin included so changing the (admin) duration re-checks the selected slot and
        // clears it if the longer appointment no longer fits.
    }, [selectedBarberId, slotDurationMin]);

    /*const bookedSlots = useMemo(() => {
        if (!selectedBarberId || !selectedDate) return new Set();
        const selectedBarber = barbers.find((b) => b.barberId == selectedBarberId);
        if (!selectedBarber) return new Set();
        return new Set(
            selectedBarber?.bookings?.filter((b) => format(new Date(b.startDateTime), "dd-MM-yyyy")
                == format(selectedDate, "dd-MM-yyyy") &&
                b.bookingId !== Number(bookingId)
            ).map((b) => format(new Date(b.startDateTime), "HH:mm")));
    }, [barbers, selectedBarberId, selectedDate, bookingId]);*/

    const handleEdit = async (outsideHoursConfirmedNow = false) => {
        const newDateFormatted = format(selectedDate, "yyyy-MM-dd");
        try {
            const originalDateAndTime = new Date(editBooking.startDateTime);
            const originalDateFormatted = format(originalDateAndTime, "yyyy-MM-dd");
            const originalTimeFormatted = format(originalDateAndTime, "HH:mm");
            const dateOrTimeChanged = newDateFormatted !== originalDateFormatted || originalTimeFormatted !== selectedTime;
            const barberChanged = editBooking?.barberId !== selectedBarberId;
            if (!dateOrTimeChanged && !barberChanged) return;
            //the reason we dont wrap it in a date object is because axios automatically
            //calls .toJSON() on Date objects which gives a string.You dont have to
            //convert it to DateTime on the backend because C# does that automatically
            //as long as the view model declares the type as DateTime
            /* Send the timestamp only when it actually moved. It used to go on every save, which the
               backend read as "this is a reschedule" - so reassigning a booking to another barber emailed
               the customer a reschedule notice for a time that hadn't changed. The backend now compares
               against the stored row either way, but there's no reason to post a field we haven't touched. */
            await adminAxios.patch(`/api/bookings/update-booking/${bookingId}`, {
                ...(dateOrTimeChanged && { StartDateTime: `${newDateFormatted}T${selectedTime}:00` }),
                BarberId: selectedBarberId,
                // Only ever true when the admin has just clicked through the out-of-hours confirmation.
                ConfirmOutsideHours: outsideHoursConfirmedNow
            })
            /* Editing takes the admin off the bookings table and back again, so the row they just changed
               is indistinguishable from the rest by the time they land. Hand the id back in history state
               and the table marks it - which matters most for a flagged booking, where they still have to
               mark it reviewed and would otherwise have to remember which one it was. */
            navigate(returnTo, { state: { recentlyEditedBookingId: Number(bookingId) } });
        }
        catch (err) {
            /* Was console-only, which meant a rejected save looked like nothing happened at all - the page
               just sat there. Now that the backend also refuses slots outside the barber's working hours,
               that silence would be the most likely outcome of a normal edit, so the reason has to reach
               the person clicking Save. */
            showToast("Couldn't save this booking", getErrorMessage(err));
        }
    }
    const handleAdminCreate = async ({ name, phone }) => {
        if (loading || bookingLoading) return;
        // Duration is validated inline on the page (adminDurationError) and gates the "Next" button, so
        // this is just a safety net. Send the raw text's value (not the last-valid number).
        const durationToSend = Number(adminDurationInput);
        if (adminDurationError) return;
        const newDateFormatted = format(selectedDate, "yyyy-MM-dd");
        console.log(`${newDateFormatted}T${selectedTime}:00`);

        setBookingLoading(true);
        try {
            await adminAxios.post('/api/bookings/create-admin-booking', {
                // For a barber this is their own id (auto-selected + locked); the backend also pins it.
                BarberId: selectedBarberId,
                StartDateTime: `${newDateFormatted}T${selectedTime}:00`,
                // Duration is now chosen on this page (adminDurationMin) rather than in the modal,
                // so the slot picker and the submitted booking always agree.
                DefaultDurationMin: durationToSend,
                FullName: name || null,
                Phone: phone,
                // Set by the out-of-hours confirmation on the previous screen (see outsideHoursConfirmed).
                ConfirmOutsideHours: outsideHoursConfirmed,
            });
            setShowModal(false);
            navigate(returnTo);
        } catch (err) {
            console.error(err.response?.data?.message || err.response?.data);
            showToast("Booking Failed", getErrorMessage(err));
        }
        finally {
            setBookingLoading(false);
        }
    };

    // User only — called from the normal booking flow
    const handleUserCreate = async () => {
        if (loading || bookingLoading) return;
        const newDateFormatted = format(selectedDate, "yyyy-MM-dd");
        setBookingLoading(true);
        try {
            // api/Bookings, plural - the controller's route. The singular spelling here 404'd, so every
            // customer booking failed with a generic "Booking Failed" toast.
            const booking = await axios.post('/api/bookings/create-pending', {
                BarberId: selectedBarberId,
                StartDateTime: `${newDateFormatted}T${selectedTime}:00`,
                // ServicesIds, not ServiceIds - the name PendingBookingCreateViewModel binds. The
                // singular spelling bound to nothing, so the request failed model validation with
                // "The ServicesIds field is required" however many services were actually chosen.
                ServicesIds: chosenServiceIds,
            });
            navigate(`/checkout/${booking.data.publicId}`);
            
        } catch (err) {
            console.error(err.response?.data?.message || err.response?.data);
            showToast("Booking Failed", getErrorMessage(err));
        }
        finally {
            setBookingLoading(false);
        }
    };

    // The actual "Next" action once any schedule warning is cleared: edit patches, staff create opens the
    // customer-details modal, customer creates the pending booking.
    const proceedFromDateTime = (outsideHoursConfirmed = false) => {
        if (isEditMode) return handleEdit(outsideHoursConfirmed);
        if (isStaffMode) return setShowModal(true);
        return handleUserCreate();
    };

    /* Staff may book outside a barber's schedule, but they have to say so - the backend refuses the slot
     * without it (409). They're committing another barber to work off-hours, or choosing to themselves.
     * Skip the warn on an unchanged edit, which would no-op anyway. */
    const handleNextClick = () => {
        if (selectedSlotOutsideSchedule() && (!isEditMode || editHasChanges())) {
            setShowScheduleWarn(true);
            return;
        }
        proceedFromDateTime();
    };

    const confirmScheduleWarn = () => {
        setShowScheduleWarn(false);
        /* Remembered for the staff-create path, where the booking isn't sent until the customer-details
         * modal is filled in - by then this confirmation is several clicks in the past. */
        setOutsideHoursConfirmed(true);
        proceedFromDateTime(true);
    };

    /* Every slot for the selected date with its display state worked out once: whether it's pickable, and
     * whether it's outside the selected barber's hours. Computed here rather than inline in the grid so
     * the legend underneath reads the SAME answers - it used to decide independently ("can this user
     * override?") and so announced an amber state on days that had no amber chip in them. Also stops
     * getAvailableSlots being recomputed once per chip. */
    const daySlots = selectedDate ? (() => {
        const free = getAvailableSlots(selectedDate);
        const isOnOriginalDate = originalDate && isSameDay(selectedDate, originalDate);
        return slotUniverse(selectedDate).map((time) => {
            /* Outside either set of hours is a hard block for customers only. For staff both stay pickable
               and are styled, so choosing one raises the confirmation rather than being silently
               unavailable. Customers can't actually reach the shop-hours case - their grid comes from
               shifts, which the backend keeps inside shop hours - but it's included so the rule the chip
               enforces is the same one the server does rather than relying on that invariant holding. */
            const isOutsideSchedule = isTimeSlotOutsideSchedule(time, selectedDate);
            const isOutsideShopHours = isTimeSlotOutsideShopHours(time, selectedDate);
            const blocked = !free.includes(time)
                || isTimeSlotClosed(time, selectedDate)
                || isTimeSlotInPast(time, selectedDate)
                // Customers also can't pick a slot inside the lead time; staff (admin/edit) only past.
                || (isCustomer && isTimeSlotTooSoon(time, selectedDate))
                || (isCustomer && (isOutsideSchedule || isOutsideShopHours));
            /* The booking's own slot stays pickable so an edit can leave the time alone - but only while
               it's still on its own barber (see isOnOriginalBarber). Reassign it and it's judged like
               any other slot. */
            const isDisabled = isEditMode
                ? (!(isOnOriginalBarber && isOnOriginalDate && time === originalTime) && blocked)
                : blocked;
            return { time, isDisabled, isOutsideSchedule, isOutsideShopHours };
        });
    })() : [];

    /* How many chips go on a row, MEASURED rather than tabulated.
     *
     * This was five hand-tuned pixel widths, one per slot step, matched to how wide the card happened to
     * be. They were wrong three times in a row - too small here, clipped there - because the thing that
     * actually decides a good layout is the card's real width and height against the number of slots, and
     * none of that is knowable from the step alone. A shop open 08:00-20:00 has twice the slots of one
     * open 09:00-13:00 at the same step, and the constants couldn't tell them apart.
     *
     * The rule: take the FEWEST columns (so the biggest chips) whose rows still fit the height. If nothing
     * fits - a 5-minute day is 100+ slots and never will - fall back to the most columns that keep chips
     * at a comfortable width and let it scroll, which is the honest outcome at that density.
     *
     * Safe from feedback loops because scrollbar-gutter reserves the bar's width whether or not it shows,
     * so a re-layout can't change the width that produced it. */
    const gridRef = useRef(null);
    const [slotCols, setSlotCols] = useState(null);
    const slotCount = daySlots.length;

    useLayoutEffect(() => {
        const el = gridRef.current;
        if (!el || slotCount === 0) return;

        const measure = () => {
            const cs = getComputedStyle(el);
            const colGap = parseFloat(cs.columnGap) || 0;
            const rowGap = parseFloat(cs.rowGap) || 0;
            const width = el.clientWidth - (parseFloat(cs.paddingLeft) || 0) - (parseFloat(cs.paddingRight) || 0);
            const height = el.clientHeight;
            if (width <= 0 || height <= 0) return;

            // Narrowest chip that still reads as a time, and shortest row that stays comfortably tappable.
            const MIN_CHIP = 72, MIN_ROW = 32;
            const maxCols = Math.max(1, Math.floor((width + colGap) / (MIN_CHIP + colGap)));

            let chosen = maxCols;
            for (let cols = 1; cols <= maxCols; cols++) {
                const rows = Math.ceil(slotCount / cols);
                if (rows * MIN_ROW + (rows - 1) * rowGap <= height) { chosen = cols; break; }
            }
            setSlotCols(chosen);
        };

        measure();
        const observer = new ResizeObserver(measure);
        observer.observe(el);
        return () => observer.disconnect();
    }, [slotCount]);

    const containerVariants = {
        hidden: {},
        visible: {
            transition: {
                delayChildren: 0.5,
                staggerChildren: 0.3
            }
        }
    };

    const itemVariants = {
        hidden: { opacity: 0, y: 60 },
        visible: { opacity: 1, y: 0, transition: { duration: 0.6, ease: "easeOut" } }
    };

    return (
        <div className="bp-page">
            <section className="datetime-hero">
                <img src="/images/BarberDateAndTimeHeroImage.avif" alt="" className="datetime-hero__bg" />
                <div className="datetime__overlay" />
                <nav className="bp-hero__nav">
                    <div className="bp-hero__nav-inner">
                        <div className="bp-hero__brand">
                            <div className="bp-hero__brand-icon">
                                <Scissors size={16} color="white" />
                            </div>
                            <span className="bp-hero__brand-name">THE FADE HOUSE</span>
                        </div>

                        <div className="bp-hero__nav-tabs">
                            <button
                                className="bp-hero__nav-btn active"
                            >
                                {isEditMode ? `${capitalize(user?.role)} Portal` : "Book Appointment"}
                            </button>
                        </div>
                    </div>
                </nav>
                <motion.div className="datetime__content"
                    initial={{ opacity: 0, y: 40 }}
                    animate={{ opacity: 1, y: 0 }}
                    transition={{ duration: 0.7, ease: "easeOut" }}>
                    <span className="datetime__eyebrow">{isEditMode ? "Edit Booking" : "Online Booking"}</span>
                    <h1 className="datetime__title">{isEditMode ? "Edit This Appointment" : "Book Your Appointment"}</h1>
                    <p className="datetime__subtitle">
                        {isEditMode ? "Edit your Barber, Date and Time as you like." : "Select your barber, date, and time — all in a few taps."}
                    </p>
                </motion.div>
            </section>
            <main className="bp-main">
                {!isStaffMode && !isEditMode && (
                    <Navlinks currentScreen="barberdatetime" />
                )}
                <div className="barber-datetime-main-next-container">
                    <motion.div className="barber-datetime-main-container" variants={containerVariants}
                        initial="hidden"
                    animate="visible">
                        {/* Choose Barber */}

                        <motion.section className="bp-section" variants={itemVariants}>
                            <h2 className="bp-section-title">Choose Your Barber</h2>
                            <p className="bp-section-sub">Select a barber to get started</p>
                            <div className="bp-barber-grid">
                                {/*barbers.length > 1 && (
                                        <motion.button
                                            key="any-available"
                                            className={`bp-barber-card ${selectedBarberId === "All" ? "selected" : ""}`}
                                            onClick={() => setSelectedBarberId("All")}
                                            whileHover={{ y: -4 }}
                                            whileTap={{ scale: 0.97 }}
                                        >
                                            <div className="bp-barber-avatar-wrap">
                                                <div className="bp-barber-avatar bp-barber-avatar--icon">
                                                    <UserRound size={32}/>
                                                </div>
                                                <span className={`bp-status-dot online`} />
                                            </div>
                                            <span className="bp-barber-name">Any Available</span>
                                            <span className={`bp-barber-status available`}>
                                                Auto-assigned
                                            </span>
                                        </motion.button>
                                    ) */}
                                {barberBookingsloading && barbers.length === 0 ? (
                                    // Availability is still loading - hold the barber grid with a spinner
                                    // rather than showing an empty "Choose Your Barber" strip on first paint.
                                    <div style={{ gridColumn: "1 / -1" }}>
                                        <LoadingSpinner message="Loading barbers" color="hsl(220, 20%, 14%)" inline />
                                    </div>
                                ) : barberBookingsError && barbers.length === 0 ? (
                                    // The load failed and we have nothing to show - a customer facing an empty
                                    // grid with no explanation can't book, so give them an error + Retry.
                                    <div style={{ gridColumn: "1 / -1" }}>
                                        <ErrorState
                                            inline
                                            title="Couldn't load barbers"
                                            message="We couldn't load availability right now. Please try again."
                                            onRetry={reFetchBarbers}
                                        />
                                    </div>
                                ) : barbers.filter((b) => !lockBarberToSelf || b.barberId === user?.barberId).map((barber) => {
                                    /*const available = isBarberAvailable(barber, selectedDate, selectedTime);*/
                                    /* when wrapping in JSX curly brackets you need to return */
                                    /* A barber locked to themselves can't change the selection - the card is shown
                                     * pre-selected but clicking is a no-op. */
                                    return (
                                        <motion.button
                                            key={barber.barberId}
                                            className={`bp-barber-card ${selectedBarberId === barber.barberId ? "selected" : ""}`}
                                            onClick={() => { if (!lockBarberToSelf) setSelectedBarberId(barber.barberId); }}
                                            whileHover={{ y: -4 }}
                                            whileTap={{ scale: 0.97 }}
                                        >
                                            <div className="bp-barber-avatar-wrap">
                                                <img src={resolveBarberImage(barber.imageUrl)} onError={handleBarberImageError} alt={barber.barberName} className="bp-barber-avatar" />
                                                <span className={`bp-status-dot online`} />
                                            </div>
                                            <span className="bp-barber-name">{barber.barberName}</span>
                                            <span className={`bp-barber-status available`}>
                                                {"Available"}
                                            </span>
                                        </motion.button>
                                    );
                                })}
                            </div>
                        </motion.section>

                        {/* Date & Time */}

                            <motion.section className="bp-section" variants={itemVariants}>
                                <h2 className="bp-section-title">Pick a Date & Time</h2>
                                <p className="bp-section-sub">Choose your preferred appointment slot</p>

                                {isStaffMode && (
                                    <div className="bp-admin-duration">
                                        <label htmlFor="bp-admin-duration-input">Booking duration (minutes)</label>
                                        <input
                                            id="bp-admin-duration-input"
                                            // Plain text (not type=number) so there are no spinner arrows;
                                            // inputMode="numeric" still brings up the number keypad on mobile,
                                            // and the onChange strips non-digits to keep it numbers-only.
                                            type="text"
                                            inputMode="numeric"
                                            value={adminDurationInput}
                                            onChange={(e) => {
                                                const raw = e.target.value.replace(/\D/g, "");
                                                setAdminDurationInput(raw);
                                                // Only commit the parsed value on valid input, so an
                                                // empty box keeps the previous duration for greying.
                                                const n = Number(raw);
                                                if (raw !== "" && Number.isFinite(n)) setAdminDurationMin(n);
                                            }}
                                        />
                                        {adminDurationError && (
                                            <span style={{ display: "block", marginTop: 6, color: "#e53e3e", fontSize: 12 }}>
                                                {adminDurationError}
                                            </span>
                                        )}
                                    </div>
                                )}

                                <div className="bp-datetime-grid">
                                    {/* Calendar */}
                                    <div className="bp-calendar-card">
                                        <div className={`bp-cal-locked-overlay ${selectedBarberId ? "hidden" : ""}`}>
                                            <Scissors size={28} />
                                            <span> Select a barber first</span>
                                        </div>

                                        <div className="bp-cal-header">
                                            <button className="bp-cal-nav" onClick={() => setCalendarMonth(subMonths(calendarMonth, 1))}><ChevronLeft size={18} /></button>
                                            <span className="bp-cal-month">{format(calendarMonth, "MMMM yyyy")}</span>
                                            <button className="bp-cal-nav" onClick={() => setCalendarMonth(addMonths(calendarMonth, 1))}><ChevronRight size={18} /></button>
                                        </div>
                                        <div className="bp-cal-weekdays">
                                            {["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"].map((d) => (
                                                <span key={d}>{d}</span>
                                            ))}
                                        </div>
                                        <div className="bp-cal-days">
                                            {calendarDays.map((day, i) => {
                                                const inMonth = isSameMonth(day, calendarMonth);
                                                const isToday = isSameDay(day, today);
                                                const isSelected = selectedDate && isSameDay(day, selectedDate);
                                                const isPast = isBefore(day, today);
                                                const isBeyondHorizon = isDateBeyondHorizon(day);
                                                const isThisDayClosed = isDateClosed(day);
                                                const isThisDayFullyBooked = isDateBooked(day);
                                                return (
                                                    <button
                                                        key={i}
                                                        className={`bp-cal-day
                                                        ${!inMonth  || isBeyondHorizon || (isThisDayClosed || (isThisDayFullyBooked && !isSameDay(day, originalDate)))? "outside" : ""}
                                                        ${isToday ? "today" : ""}
                                                        ${isSelected ? "selected" : ""}
                                                        ${isPast && !isToday ? "past" : ""}
                                                    `}
                                                        onClick={() => {
                                                            if (inMonth && !isPast && !isBeyondHorizon && !isThisDayClosed && (!isThisDayFullyBooked || isSameDay(day, originalDate))) {
                                                                setSelectedDate(day);
                                                                if (selectedTime && selectedTime !== originalTime) {
                                                                    const isFree = getAvailableSlots(day).includes(selectedTime);
                                                                    const isClosed = isTimeSlotClosed(selectedTime, day);
                                                                    const isPast = isTimeSlotInPast(selectedTime, day);
                                                                    if (!isFree || isClosed || isPast) {
                                                                        setSelectedTime(null);
                                                                    }
                                                                }
                                                            }
                                                        }}
                                                        disabled={!inMonth || isPast || isBeyondHorizon || isThisDayClosed || (isThisDayFullyBooked && !isSameDay(day, originalDate))}
                                                    >
                                                        {format(day, "d")}
                                                    </button>
                                                );
                                            })}
                                        </div>
                                        <div className="bp-cal-legend">
                                            <span><i className="legend-dot selected" /> Selected</span>
                                            <span><i className="legend-dot today" /> Today</span>
                                            <span><i className="legend-dot closed" /> Closed</span>
                                        </div>
                                    </div>

                                    {/* Time Slots. The wrapper is the grid item, so the calendar beside it
                                        sets the row height and a long slot list can't stretch it - see
                                        .bp-time-card-slot. */}
                                    <div className="bp-time-card-slot">
                                    <div className="bp-time-card">
                                        {/* Staff only, and never for customers, who can't override either boundary and
                                            would only be shown slots the backend will refuse. Off by default: the whole
                                            day is 48 chips at a 30-minute step and 96 at 15, so it's the exception
                                            (opening specially for a regular) rather than the view.

                                            Outside the "has a date been picked?" branch below, and it has to be. A day
                                            the shop is closed has an empty grid, which makes isDateBooked true and
                                            DISABLES that day in the calendar - so with the toggle inside that branch,
                                            reaching it needed a selected date and selecting the date needed the toggle.
                                            Opening specially on a Sunday, the case it exists for, was the one case it
                                            could not do. */}
                                        {!isCustomer && (
                                            <label className="bp-extended-hours">
                                                <input
                                                    type="checkbox"
                                                    checked={showExtendedHours}
                                                    onChange={(e) => setShowExtendedHours(e.target.checked)}
                                                />
                                                <span>Show hours outside the shop's opening times</span>
                                            </label>
                                        )}
                                        {selectedDate ? (
                                            <>
                                                <h3 className="bp-time-heading">{format(selectedDate, "EEEE, MMMM d")}</h3>
                                                <p className="bp-time-sub">Available slots</p>
                                                {/* Column count is measured, not configured - see the useLayoutEffect
                                                    above. Until the first measurement lands the CSS fallback applies,
                                                    which is why this is an override and not the only rule. */}
                                                <div
                                                    ref={gridRef}
                                                    className="bp-time-grid"
                                                    style={slotCols ? { gridTemplateColumns: `repeat(${slotCols}, minmax(0, 1fr))` } : undefined}
                                                >
                                                    {daySlots.map(({ time, isDisabled, isOutsideSchedule, isOutsideShopHours }) => (
                                                        <button
                                                            key={time}
                                                            className={`bp-time-chip ${selectedTime === time ? "selected" : ""} ${isDisabled ? "booked" : ""} ${!isDisabled && isOutsideShopHours ? "outside-shop-hours" : !isDisabled && isOutsideSchedule ? "outside-hours" : ""}`}
                                                            onClick={() => !isDisabled && setSelectedTime(time)}
                                                            disabled={isDisabled}
                                                            /* Shop hours win the styling when both apply: it's the wider
                                                               problem (the shop is shut, so who is rostered is moot), and
                                                               two dashed borders on one chip say nothing useful. The
                                                               tooltip still names both, and names WHICH way each is
                                                               breached - "would run past closing (17:30)" rather than
                                                               "outside opening hours" for a 17:15 start that is plainly
                                                               inside them. Same phrase builders as the confirmation
                                                               modal, so the two can't describe a slot differently. */
                                                            title={isDisabled ? undefined : (() => {
                                                                const dayName = format(selectedDate, "EEEE");
                                                                const parts = [
                                                                    shopBreachPhrase(shopHoursBreach(time, selectedDate), dayName),
                                                                    barberBreachPhrase(barberHoursBreach(time, selectedDate),
                                                                        `${selectedBarberName} isn't`, `${selectedBarberName}'s`, dayName),
                                                                ].filter(Boolean);
                                                                if (parts.length === 0) return undefined;
                                                                const s = parts.join(", and ");
                                                                return s.charAt(0).toUpperCase() + s.slice(1);
                                                            })()}
                                                        >
                                                            {time}
                                                        </button>
                                                    ))}
                                                </div>
                                                {/* Each row only shows when there is actually a chip on screen in that state
                                                    to explain. A barber on their own full-day schedule has none, and a legend
                                                    for a state the grid isn't in just makes the reader hunt for something
                                                    that isn't there. Same dot/label shape as the calendar legend above. */}
                                                {daySlots.some(s => !s.isDisabled && (s.isOutsideSchedule || s.isOutsideShopHours)) && (
                                                    <div className="bp-time-legend">
                                                        {daySlots.some(s => !s.isDisabled && !s.isOutsideShopHours && s.isOutsideSchedule) && (
                                                            <span><i className="legend-dot outside-hours" /> Outside working hours</span>
                                                        )}
                                                        {daySlots.some(s => !s.isDisabled && s.isOutsideShopHours) && (
                                                            <span><i className="legend-dot outside-shop-hours" /> Outside shop opening hours</span>
                                                        )}
                                                    </div>
                                                )}
                                            </>
                                        ) : (
                                            <div className="bp-time-placeholder">
                                                <Calendar size={30} />
                                                <p>Select a date to see available times</p>
                                            </div>
                                        )}
                                    </div>
                                    </div>
                                </div>
                            </motion.section>
                    </motion.div>
                    <div className="barber-datetime-proceed">
                        <button onClick={handleNextClick} disabled={loading || bookingLoading || !(selectedBarberId && selectedDate && selectedTime) || (isStaffMode && !!adminDurationError)} className={`next-details-btn ${!(selectedBarberId && selectedDate && selectedTime) || (isStaffMode && !!adminDurationError) ? "disabled" : ""}`}>
                            {isEditMode ? (
                                <>
                                    Confirm Edit <SquarePen size={18} />
                                </>
                            ) : (
                                <>
                                    Next: {isStaffMode ? "User's Details" : "Your Details"} <ArrowRight size={18} />
                                </>
                            )}
                        </button>
                    </div>
                </div>
            </main>
            {showModal && isStaffMode && (
                <UserFormModal
                    onConfirm={handleAdminCreate}
                    onCancel={handleCancel}/>
            )}
            {/* The one place an out-of-hours booking can be agreed to. The backend refuses the slot unless
                this has been clicked, so it isn't advisory - it's the decision itself. */}
            {showScheduleWarn && (() => {
                /* Which boundaries are actually being crossed decides the whole message. "Outside working
                   hours" on a slot that is really outside the SHOP's hours would have the staff member
                   checking a rota that has nothing to do with the problem - and agreeing to open the shop
                   late is a different decision from agreeing to work a shift, made by a different person.
                   The barber half keeps its existing split: a barber is locked to their own chair, so they
                   are always agreeing for themselves and get the soft copy, while an admin is always
                   committing someone else and gets "make sure they've agreed". */
                const dayName = selectedDate ? format(selectedDate, "EEEE") : "that day";
                const when = `${selectedTime} on ${selectedDate ? format(selectedDate, "EEEE, MMMM d") : ""}`;
                const checkWith = isBookingSelf
                    ? "do you want to work this slot?"
                    : "make sure they've agreed to work this slot.";

                /* The same two phrase builders the chips' tooltips use, so a slot is described identically
                   wherever it is explained. Each names the strongest true thing: a whole day off reads
                   differently from a slot merely outside a shift, a shop that never opens that day
                   differently from one not open YET, and both of those differently again from an
                   appointment that starts inside the hours and simply runs past the end of them. */
                const shopBreach = shopHoursBreach(selectedTime, selectedDate);
                const barberBreach = barberHoursBreach(selectedTime, selectedDate);
                const shopClause = shopBreachPhrase(shopBreach, dayName);
                const barberClause = barberBreachPhrase(
                    barberBreach,
                    isBookingSelf ? "you're" : `${selectedBarberName} isn't`,
                    isBookingSelf ? "your" : `${selectedBarberName}'s`,
                    dayName);

                /* "Runs past closing" is a smaller thing to agree to than "the shop is shut", and the
                   heading is the first word on it - so it says which. */
                const heading = shopBreach?.kind === "closed" ? "The shop is closed that day"
                    : shopBreach?.kind === "overruns" && !barberClause ? "Runs past closing time"
                    : shopClause && barberClause ? "Outside shop and working hours"
                    : shopClause ? "Outside shop opening hours"
                    : barberBreach?.kind === "day-off" ? "That's a day off"
                    : barberBreach?.kind === "overruns" ? "Runs past the end of the shift"
                    : "Outside working hours";

                /* An overrun doesn't need the shop to "open specially" - it needs it to stay a little
                   late, which is a different ask and the one GraceMinutesAfterClose exists to pre-approve. */
                const shopConsequence = shopBreach?.kind === "overruns"
                    ? "the shop will have to stay open a little later"
                    : "the shop will have to open specially";

                const body = shopClause && barberClause
                    ? `${when}: ${shopClause}, and ${barberClause}. You can still book it, but ${shopConsequence} — and ${checkWith}`
                    : shopClause
                        ? `${when}: ${shopClause}. You can still book it, but ${shopConsequence} for it.`
                        : `${when}: ${barberClause}. You can still book it${isBookingSelf ? " — " : ", but "}${checkWith}`;
                const confirmLabel = shopClause && !barberClause ? "Book outside opening hours"
                    : isBookingSelf ? "Book anyway"
                    : "Book outside hours";

                return (
                <div className="modal-overlay" onClick={() => setShowScheduleWarn(false)}>
                    <div className="user-form-modal" onClick={(e) => e.stopPropagation()}>
                        <h2>{heading}</h2>
                        <p>{body}</p>
                        <div className="user-form-modal-actions">
                            <button className="user-form-btn-cancel" onClick={() => setShowScheduleWarn(false)}>Go back</button>
                            <button className="user-form-btn-confirm" onClick={confirmScheduleWarn}>
                                {confirmLabel}
                            </button>
                        </div>
                    </div>
                </div>
                );
            })()}
        </div>
    );
}
export default BarberDateAndTime;