import { useState, useMemo, useEffect, useContext } from "react";
import UserFormModal from "./UserFormModal/UserFormModal.jsx";
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

const BarberDateAndTime = () => {
    const [barbers, setBarbers] = useState([]);
    const [shopWideClosures, setShopWideClosures] = useState([]);
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
    } = useContext(BookingDetailsContext);
    //if the admin forgets to logout and manually set adminBooking to be true
    //then it still could be so that when he makes a booking as a normal user
    //he doesnt get the normal experience. Just validate on backend then.

    /* what this does is it effectively converts
        bookingId to a boolean. If its not falsy("", false, 0, null, undefined), 
        isEditMode will be set to true*/
    const { data: barberBookings, loading: barberBookingsloading } = useFetch(`/api/Barbers/barbers-with-bookings`, isEditMode);
    //this will fetch dates where barbers are booked, when they are closed and when the whole shop is closed
    const { data: editBooking, loading: editBookingloading } = useFetch(bookingId ? `/api/Bookings/admin/${bookingId}` : null, isEditMode);



    //this so when the user picks a barber, date and time but then he selects another barber and turns out that that barber has that slot fully booked, this will detect a change
    //in the barber and see whether that time slot is booked. If so then time will be set to null


    useEffect(() => {
        if (!barberBookings) return;//this if statement is very important. without it you are calling undefined?.barbers which will cause the page to not render
        console.log(barberBookings);
        setBarbers(barberBookings?.barbers);
        setShopWideClosures(barberBookings?.shopClosures);
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


    const capitalize = (str) => str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();

    const today = startOfDay(new Date());

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
                const slotEnd = addMinutes(slotStart, 30);

                return barbers.some(b => {
                    const bookingsOnDay = b?.bookings?.filter(bk => isSameDay(new Date(bk.startDateTime), date));
                    return !bookingsOnDay.some(bk => {
                        const bkStart = new Date(bk.startDateTime);
                        const bkEnd = addMinutes(bkStart, bk.durationMin);
                        return slotStart < bkEnd && slotEnd > bkStart;
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
            const slotEnd = addMinutes(slotStart, 30);

            return !barberBookingsOnDay.some(bk => {
                /* This is the filter's return. It returns true or false accordingly
                */
                const bkStart = new Date(bk.startDateTime);
                const bkEnd = addMinutes(bkStart, bk.durationMin);
                return slotStart < bkEnd && slotEnd > bkStart
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

    const isTimeSlotInPast = (time, date = selectedDate) => {
        if (!date) return false;
        const [hours, minutes] = time.split(":").map(Number);
        const slotDateTime = new Date(date);
        slotDateTime.setHours(hours, minutes, 0, 0);
        return slotDateTime < new Date();
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
    }, [selectedBarberId]);

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
    const handleAdminCreate = async ({ name, phone, duration }) => {
        if (loading || bookingLoading) return;
        const newDateFormatted = format(selectedDate, "yyyy-MM-dd");
        console.log(`${newDateFormatted}T${selectedTime}:00`);

        setBookingLoading(true);
        try {
            const booking = await adminAxios.post('/api/bookings/create-admin-booking', {
                BarberId: selectedBarberId,
                StartDateTime: `${newDateFormatted}T${selectedTime}:00`,
                DefaultDurationMin: parseInt(duration),
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
                                                <img src={barber.imageUrl.startsWith("http") ? barber.imageUrl : `${import.meta.env.VITE_BASE_URL}${barber.imageUrl}`} alt={barber.barberName} className="bp-barber-avatar" />
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
                                                const isThisDayClosed = isDateClosed(day);
                                                const isThisDayFullyBooked = isDateBooked(day);
                                                return (
                                                    <button
                                                        key={i}
                                                        className={`bp-cal-day 
                                                        ${!inMonth  || (isThisDayClosed || (isThisDayFullyBooked && !isSameDay(day, originalDate)))? "outside" : ""} 
                                                        ${isToday ? "today" : ""} 
                                                        ${isSelected ? "selected" : ""} 
                                                        ${isPast && !isToday ? "past" : ""}
                                                    `}
                                                        onClick={() => {
                                                            if (inMonth && !isPast && !isThisDayClosed && (!isThisDayFullyBooked || isSameDay(day, originalDate))) {
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
                                                        disabled={!inMonth || isPast || isThisDayClosed || (isThisDayFullyBooked && !isSameDay(day, originalDate))}
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
                                                        const isOnOriginalDate = selectedDate && originalDate && isSameDay(selectedDate, originalDate);
                                                        const isDisabled = isEditMode
                                                            ? (!(isOnOriginalDate && time === originalTime) && (!isFree || isClosed || isPast))
                                                            : (!isFree || isClosed || isPast);
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