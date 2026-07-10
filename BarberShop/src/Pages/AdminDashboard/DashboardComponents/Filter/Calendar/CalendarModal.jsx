import "./CalendarModal.css";
import { useMemo, useState } from "react";
import { ArrowRight, ArrowLeft } from "lucide-react";
import { startOfMonth, endOfMonth, startOfWeek, endOfWeek, startOfDay, addDays , isSameDay, isBefore, isAfter, isSameMonth, format, subMonths, addMonths, endOfDay} from "date-fns";
const CalendarModal = ({mode, setSelectedFromDate, setSelectedToDate, selectedToDate, selectedFromDate}) => {
    const [calendarMonth, setCalendarMonth] = useState(new Date());
    const today = startOfDay(new Date());
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

    return (
        <div className="modal-calendar">
            <div className="modal-calendar-nav">
                <button type="button" className="modal-calendar-nav-btn" onClick={() => setCalendarMonth(subMonths(calendarMonth, 1))} >
                    <ArrowLeft size={18}/>
                </button>
                <span className="modal-calendar-month">Mar 2026</span>
                <button type="button" className="modal-calendar-nav-btn" onClick={() => setCalendarMonth(addMonths(calendarMonth, 1))} >
                    <ArrowRight size={18}/>
                </button>
            </div>

            <div className="modal-calendar-grid">
                <div className="modal-calendar-weekday">S</div>
                <div className="modal-calendar-weekday">M</div>
                <div className="modal-calendar-weekday">T</div>
                <div className="modal-calendar-weekday">W</div>
                <div className="modal-calendar-weekday">T</div>
                <div className="modal-calendar-weekday">F</div>
                <div className="modal-calendar-weekday">S</div>
            </div>


            <div className="modal-calendar-days">
                {calendarDays.map((day, i) => {
                    const isToday = isSameDay(day, today);
                    const isPast = mode === "To" && selectedFromDate && isBefore(day, selectedFromDate);
                    const isFuture = mode === "From" && selectedToDate && isAfter(day, selectedToDate);
                    /* !isFuture = mode !== "From" || !selectedToDate || !isAfter(day, selectedToDate) */
                    const inMonth = isSameMonth(day, calendarMonth);
                    const isDateSelected =
                        (selectedFromDate && isSameDay(selectedFromDate, day)) ||
                        (selectedToDate && isSameDay(day, selectedToDate));
                    return (
                        <button
                            key={i}
                            className={`modal-calendar-day ${!inMonth ? "outside" : ""} ${isToday ? "today" : ""} ${isDateSelected ? "selected" : ""} ${(isPast || isFuture) ? "outside" : ""}`}
                            onClick={() => {
                                if (inMonth) {
                                    if (mode === "To" && !isPast) {
                                        setSelectedToDate(isSameDay(day, selectedToDate) ? null :endOfDay(day));
                                    }
                                    else if (mode === "From" && !isFuture) {
                                        setSelectedFromDate(isSameDay(day, selectedFromDate) ? null : day);
                                    }
                                }
                            }}
                            disabled={!inMonth || isPast || isFuture}
                        >
                            {format(day, "d")}
                        </button>
                    );
                }) }
            </div>
            <div className="modal-calendar-footer">
                <span className="modal-calendar-selected-badge">Mar 19, 2026</span>
            </div>
        </div>
    );
}
export default CalendarModal;