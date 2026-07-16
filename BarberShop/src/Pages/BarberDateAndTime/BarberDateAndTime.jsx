import { useState, useMemo, useEffect, useContext } from "react";
import UserFormModal from "./UserFormModal/UserFormModal.jsx";
import { resolveBarberImage, handleBarberImageError } from "../../utils/barberImage.js";
import { useNavigate, useParams, useSearchParams } from "react-router-dom";
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
import "./BarberDateAndTime.css";
import { adminAxios } from "../../Hooks/AxiosInterceptor";
import { AuthContext } from "../../Context/AuthContext.jsx";
import { ToastContext } from "../../Context/ToastContext.jsx";
import Navlinks from "../../Components/NavLinks/Navlinks";
import { BookingDetailsContext } from "../../Context/BookingDetailsContext.jsx";

const TIME_SLOTS = [
    "09:00", "09:30", "10:00", "10:30",
    "11:00", "11:30", "12:00", "12:30",
    "13:00", "13:30", "14:00", "14:30",
    "15:00", "15:30", "16:00", "16:30",
    "17:00", "17:30",
];

/* Customer-only booking window, mirroring the backend's ValidateBookingTime (BookingsController.cs):
 * customers must book at least 90 min ahead and at most 60 days out. Staff (admin/reschedule) are exempt. */
const MIN_ADVANCE_MINUTES = 90;
const MAX_ADVANCE_DAYS = 60;
/* Shop closing time as minutes-from-midnight, mirroring the backend ShopClose (17:30) in
 * BookingsController.cs. A customer booking must end by close + grace, so late start slots that
 * wouldn't fit are hidden (staff are exempt). */
const SHOP_CLOSE_MINUTES = 17 * 60 + 30;

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
    const [calendarMonth, setCalendarMonth] = useState(new Date());
    const { bookingId } = useParams();
    const isEditMode = !!bookingId;
    const [originalTime, setOriginalTime] = useState(null);
    const [originalDate, setOriginalDate] = useState(null);
    const navigate = useNavigate();
    const { user, loading } = useContext(AuthContext);
    const [searchParams] = useSearchParams();
    const isAdminBooking = searchParams.get("adminBooking") === "true";
    const isAdminMode = user?.role === "ADMIN" && isAdminBooking;
    const [showModal, setShowModal] = useState(false);
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
    const { data: barberBookings, loading: barberBookingsloading } = useFetch(`/api/Barbers/barbers-with-bookings`, isEditMode);
    //this will fetch dates where barbers are booked, when they are closed and when the whole shop is closed
    const { data: editBooking, loading: editBookingloading } = useFetch(bookingId ? `/api/Bookings/admin/${bookingId}` : null, isEditMode);
    /* Admin-only: pull the default booking duration so the on-page duration control starts at the
     * shop's configured default (admin auth is required, so this uses adminAxios via isProtected). */
    const { data: shopSettings } = useFetch(isAdminMode ? `/api/Settings` : null, true);



    //this so when the user picks a barber, date and time but then he selects another barber and turns out that that barber has that slot fully booked, this will detect a change
    //in the barber and see whether that time slot is booked. If so then time will be set to null


    useEffect(() => {
        if (!barberBookings) return;//this if statement is very important. without it you are calling undefined?.barbers which will cause the page to not render
        console.log(barberBookings);
        setBarbers(barberBookings?.barbers);
        setShopWideClosures(barberBookings?.shopClosures);
        setBufferMin(barberBookings?.bufferMin ?? 0);
        setGraceMinutesAfterClose(barberBookings?.graceMinutesAfterClose ?? 0);
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

    /* The length of the booking being placed, used to grey slots by the real appointment length.
     * Edit: the existing booking's duration. Admin: the on-page control. Customer: the chosen
     * services' total (falls back to 30 until the Services page populates it). */
    const DEFAULT_SLOT_MIN = 30;
    const slotDurationMin =
        isEditMode ? (editBooking?.durationMin ?? DEFAULT_SLOT_MIN)
        // Uses the last VALID admin duration, so greying stays put (e.g. at 60) while the field is
        // temporarily empty during editing rather than snapping to the default.
        : isAdminMode ? (adminDurationMin || DEFAULT_SLOT_MIN)
        : (chosenServicesDurationMin || DEFAULT_SLOT_MIN);

    /* Suggested values for the admin duration input's datalist. The field is a free number input,
     * so the admin can also type any value (e.g. a one-off 75) - these are just quick picks. */
    const DURATION_PRESETS = [15, 30, 45, 60, 90];

    const capitalize = (str) => str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();

    const today = startOfDay(getMaltaNow());

    /* The 90-min buffer and 60-day horizon are a customer-only rule; staff booking (admin mode) and staff
     * rescheduling (edit mode) are only blocked from picking a past slot - matching the backend. */
    const isCustomer = !isAdminMode && !isEditMode;
    const maxCustomerDate = isCustomer ? addDays(today, MAX_ADVANCE_DAYS) : null;
    const isDateBeyondHorizon = (day) => !!maxCustomerDate && isAfter(startOfDay(day), maxCustomerDate);

    /*const isBarberAvailable = (barber, selectedDate, selectedTime) => {
        if (!selectedDate || !selectedTime) return true; // no date and time yet, show all
        const selectedDateOnly = format(selectedDate, "yyyy-MM-dd");

        //check closures
        const isClosed = barber.dateClosures?.some(c => {
            if (c.date !== selectedDateOnly) return false;
            if (c.isFullDay) return true;
            return c.startTime.slice(0, 5) <= selectedTime && c.EndTime.slice(0, 5) > selectedTime;
            //the reason we clice is because TimeOnly from the backend returns in this format
            //HH:mm:ss and lets say selectedTime is "10:00". Comparing "10:00" with "10:00:00" is unreliable
        });

        const isBooked = barber?.bookings?.some(b =>
            format(new Date(b.startDateTime), "yyyy-MM-dd HH:mm") === `${selectedDateOnly} ${selectedTime}`
        );

        return !isClosed && !isBooked;//if barber is not closed and not booked return true
    }*/


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
            const start = new Date(c.startDate);
            const end = c.endDate ? new Date(c.endDate) : start;
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
    const getAvailableSlots = (date) => {
        if (!date) return [];
        if (!selectedBarberId) return TIME_SLOTS;
        if (selectedBarberId === "All") {
            return TIME_SLOTS.filter(slot => {
                const slotStart = parse(slot, "HH:mm", date);
                const slotEnd = addMinutes(slotStart, slotDurationMin);

                return barbers.some(b => {
                    const bookingsOnDay = b?.bookings?.filter(bk => isSameDay(new Date(bk.startDateTime), date));
                    return !bookingsOnDay.some(bk => {
                        const bkStart = new Date(bk.startDateTime);
                        /* Expand each booking's blocked window by the buffer on both sides so a slot
                         * within `bufferMin` of a booking's start or end is greyed out, mirroring the
                         * backend overlap check. */
                        const bkBlockStart = addMinutes(bkStart, -bufferMin);
                        const bkEnd = addMinutes(bkStart, bk.durationMin + bufferMin);
                        return slotStart < bkEnd && slotEnd > bkBlockStart;
                    });
                });
            });
        }
        const barber = barbers.find(b => selectedBarberId === b.barberId);
        const barberBookingsOnDay = barber?.bookings?.filter(bk => isSameDay(new Date(bk.startDateTime), date));
        return TIME_SLOTS.filter(slot => {
            /* this returns the filtered array of available slots back to whoever 
            called getAvailableSlots*/
            const slotStart = parse(slot, "HH:mm", date);
            /* parse returns a date object. It returns something like this:
            2026-08-11T09:00:00.000
            The format part tells parse how to read the string. Without it,
            parse wouldnt know what each part of the string means.
            For example: "9:00 AM" -> h = hour(9), mm = minutes(00), aa = AM*/
            const slotEnd = addMinutes(slotStart, slotDurationMin);

            return !barberBookingsOnDay.some(bk => {
                /* This is the filter's return. It returns true or false accordingly
                */
                const bkStart = new Date(bk.startDateTime);
                /* Blocked window expanded by the buffer on both sides (see the "All" branch above)
                 * so the picker matches the backend's between-booking gap. */
                const bkBlockStart = addMinutes(bkStart, -bufferMin);
                const bkEnd = addMinutes(bkStart, bk.durationMin + bufferMin);
                return slotStart < bkEnd && slotEnd > bkBlockStart
                /* This is the some's return. For each booking, it returns true or false.
                If any booking returns true this means there is an overlap*/

                /* for each slot:
                     for each booking on that day:
                        does this booking overlap the slot? -> third return
                     does ANY booking overlap? -> second return ("!" flips it: "is slot free?"
                     return only the slots that are free*/
            })
        })
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

    /* Customer-only: within the 90-min lead time. This also covers "in the past" (past is < now < now+90),
     * so for customers it fully subsumes isTimeSlotInPast. */
    const isTimeSlotTooSoon = (time, date = selectedDate) => {
        if (!date) return false;
        return slotDateTimeOf(time, date) < addMinutes(getMaltaNow(), MIN_ADVANCE_MINUTES);
    };

    /* Customer-only: the booking must finish by close + grace (mirrors the backend WithinWorkingHours).
     * Staff (admin/edit) can book past close, so they're exempt. Uses slotDurationMin (duration-aware). */
    const isTimeSlotAfterClose = (time) => {
        if (!isCustomer) return false;
        const [hours, minutes] = time.split(":").map(Number);
        return hours * 60 + minutes + slotDurationMin > SHOP_CLOSE_MINUTES + graceMinutesAfterClose;
    };

    const isTimeSlotClosed = (time, date = selectedDate) => {
        /* partial closures can never be multi-day so just keep checking startDate and ignore endDate because it is irrelevant here */
        if (!date) return false;
        if (!selectedBarberId) return false;
        const dateStr = format(date, "yyyy-MM-dd");//dont change this format because dateOnly stores it in this format
        const shopWide = shopWideClosures.filter(c => !c.isFullDay && c.startDate === dateStr).some(c => time >= c.startTime.slice(0, 5) && time < c.endTime.slice(0, 5));
        if (selectedBarberId === "All") return shopWide;
        let barberBlocked = false;
        const selectedBarber = barbers.find(b => b.barberId === selectedBarberId);
        barberBlocked = selectedBarber?.dateClosures.filter(c => !c.isFullDay && c.startDate === dateStr)
            .some(c => time >= c.startTime.slice(0, 5) && time < c.endTime.slice(0, 5));
        //the reason we clice is because TimeOnly from the backend returns in this format
        //HH:mm:ss and lets say selectedTime is "10:00". Comparing "10:00" with "10:00:00" is unreliable

        return shopWide || barberBlocked;
    }

    useEffect(() => {
        if (selectedTime && selectedDate) {
            const isFree = getAvailableSlots(selectedDate).includes(selectedTime);
            const isClosed = isTimeSlotClosed(selectedTime);
            const isPast = isTimeSlotInPast(selectedTime);
            if (!isFree || isClosed || isPast) {
                if (selectedTime !== originalTime) setSelectedTime(null);
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

    const handleEdit = async () => {
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
            await adminAxios.patch(`/api/bookings/update-booking/${bookingId}`, {
                StartDateTime: `${newDateFormatted}T${selectedTime}:00`,
                BarberId: selectedBarberId
            })
            navigate("/admin");
        }
        catch (err) {
            console.error(err.response?.data?.message || "Something went wrong");
        }
    }
    const handleAdminCreate = async ({ name, phone }) => {
        if (loading || bookingLoading) return;
        // Validate the raw text (not the last-valid number) so an empty/blank box is still blocked.
        const durationToSend = Number(adminDurationInput);
        if (!Number.isInteger(durationToSend) || durationToSend < 5 || durationToSend > 240) {
            showToast("Booking Failed", "Please enter a booking duration between 5 and 240 minutes");
            return;
        }
        const newDateFormatted = format(selectedDate, "yyyy-MM-dd");
        console.log(`${newDateFormatted}T${selectedTime}:00`);

        setBookingLoading(true);
        try {
            const booking = await adminAxios.post('/api/bookings/create-admin-booking', {
                BarberId: selectedBarberId,
                StartDateTime: `${newDateFormatted}T${selectedTime}:00`,
                // Duration is now chosen on this page (adminDurationMin) rather than in the modal,
                // so the slot picker and the submitted booking always agree.
                DefaultDurationMin: durationToSend,
                FullName: name || null,
                Phone: phone,
            });
            setShowModal(false);
            navigate(`/admin/${booking.data.id}`);
        } catch (err) {
            console.error(err.response?.data?.message || err.response?.data);
            showToast("Booking Failed", err.response?.data?.message || "An unexpected error occurred");
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
            const booking = await axios.post('/api/booking/create-pending', {
                BarberId: selectedBarberId,
                StartDateTime: `${newDateFormatted}T${selectedTime}:00`,
                ServiceIds: chosenServiceIds,
            });
            navigate(`/checkout/${booking.data.id}`);
            
        } catch (err) {
            console.error(err.response?.data?.message || err.response?.data);
            showToast("Booking Failed", err.response?.data?.message || "An unexpected error occurred");
        }
        finally {
            setBookingLoading(false);
        }
    };
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
                {!isAdminMode && !isEditMode && (
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
                                {barbers.map((barber) => {
                                    /*const available = isBarberAvailable(barber, selectedDate, selectedTime);*/
                                    /* when wrapping in JSX curly brackets you need to return */
                                    return (
                                        <motion.button
                                            key={barber.barberId}
                                            className={`bp-barber-card ${selectedBarberId === barber.barberId ? "selected" : ""}`}
                                            onClick={() => setSelectedBarberId(barber.barberId)}
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

                                {isAdminMode && (
                                    <div className="bp-admin-duration">
                                        <label htmlFor="bp-admin-duration-input">Booking duration (minutes)</label>
                                        <input
                                            id="bp-admin-duration-input"
                                            type="number"
                                            min={5}
                                            max={240}
                                            step={5}
                                            list="bp-duration-presets"
                                            value={adminDurationInput}
                                            onChange={(e) => {
                                                const raw = e.target.value;
                                                setAdminDurationInput(raw);
                                                // Only commit the parsed value on valid input, so an
                                                // empty box keeps the previous duration for greying.
                                                const n = Number(raw);
                                                if (raw !== "" && Number.isFinite(n)) setAdminDurationMin(n);
                                            }}
                                        />
                                        <datalist id="bp-duration-presets">
                                            {DURATION_PRESETS.map((min) => (
                                                <option key={min} value={min} />
                                            ))}
                                        </datalist>
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

                                    {/* Time Slots */}
                                    <div className="bp-time-card">
                                        {selectedDate ? (
                                            <>
                                                <h3 className="bp-time-heading">{format(selectedDate, "EEEE, MMMM d")}</h3>
                                                <p className="bp-time-sub">Available slots</p>
                                                <div className="bp-time-grid">
                                                    {TIME_SLOTS.map((time) => {
                                                        const isFree = getAvailableSlots(selectedDate).includes(time);
                                                        const isClosed = isTimeSlotClosed(time);
                                                        const isPast = isTimeSlotInPast(time);
                                                        /* Customers also can't pick a slot inside the 90-min lead time; staff (admin/edit) only past. */
                                                        const isTooSoon = isCustomer && isTimeSlotTooSoon(time);
                                                        /* Customers can't pick a slot that would run past close + grace (staff exempt). */
                                                        const isAfterClose = isTimeSlotAfterClose(time);
                                                        const isOnOriginalDate = selectedDate && originalDate && isSameDay(selectedDate, originalDate);
                                                        const blocked = !isFree || isClosed || isPast || isTooSoon || isAfterClose;
                                                        const isDisabled = isEditMode
                                                            ? (!(isOnOriginalDate && time === originalTime) && blocked)
                                                            : blocked;
                                                        return (
                                                            <button
                                                                key={time}
                                                                className={`bp-time-chip ${selectedTime === time ? "selected" : ""} ${isDisabled ? "booked" : ""}`}
                                                                onClick={() => !isDisabled && setSelectedTime(time)}
                                                                disabled={isDisabled}
                                                            >
                                                                {time}
                                                            </button>
                                                        );
                                                    })}
                                                </div>
                                            </>
                                        ) : (
                                            <div className="bp-time-placeholder">
                                                <Calendar size={30} />
                                                <p>Select a date to see available times</p>
                                            </div>
                                        )}
                                    </div>
                                </div>
                            </motion.section>                     
                    </motion.div>
                    <div className="barber-datetime-proceed">
                        <button onClick={isEditMode ? handleEdit : isAdminMode ? () => setShowModal(true) : handleUserCreate} disabled={loading || bookingLoading || !(selectedBarberId && selectedDate && selectedTime)} className={`next-details-btn ${!(selectedBarberId && selectedDate && selectedTime) ? "disabled" : ""}`}>
                            {isEditMode ? (
                                <>
                                    Confirm Edit <SquarePen size={18} />
                                </>
                            ) : (
                                <>
                                    Next: {isAdminMode ? "User's Details" : "Your Details"} <ArrowRight size={18} />
                                </>
                            )}
                        </button>
                    </div>
                </div>
            </main>
            {showModal && isAdminMode && (
                <UserFormModal
                    onConfirm={handleAdminCreate}
                    onCancel={handleCancel}/>
            )}
        </div>
    );
}
export default BarberDateAndTime;