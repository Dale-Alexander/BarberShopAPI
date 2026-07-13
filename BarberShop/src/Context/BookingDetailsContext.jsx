import { createContext, useState, useEffect } from "react";

export const BookingDetailsContext = createContext();

export const BookingDetailsContextProvider = ({ children }) => {
    const [selectedBarberId, setSelectedBarberId] = useState(
        () => JSON.parse(sessionStorage.getItem("selectedBarberId")) || null
    );
    const [selectedDate, setSelectedDate] = useState(
        () => {
            const d = sessionStorage.getItem("selectedDate");
            return d ? new Date(d) : null;
        }
    );
    const [selectedTime, setSelectedTime] = useState(
        () => sessionStorage.getItem("selectedTime") || null
    );
    const [chosenServiceIds, setChosenServiceIds] = useState(
        () => JSON.parse(sessionStorage.getItem("chosenServiceIds")) || []
    );
    /* Total duration (minutes) of the chosen services, so the date/time picker greys slots by the
     * real appointment length instead of a flat 30. The Services-selection page sets this alongside
     * chosenServiceIds; until then it stays 0 and the picker falls back to its default. */
    const [chosenServicesDurationMin, setChosenServicesDurationMin] = useState(
        () => JSON.parse(sessionStorage.getItem("chosenServicesDurationMin")) || 0
    );

    useEffect(() => {
        sessionStorage.setItem("selectedBarberId", JSON.stringify(selectedBarberId));
    }, [selectedBarberId]);

    useEffect(() => {
        if (selectedDate) sessionStorage.setItem("selectedDate", selectedDate.toISOString());
        else sessionStorage.removeItem("selectedDate");
    }, [selectedDate]);

    useEffect(() => {
        if (selectedTime) sessionStorage.setItem("selectedTime", selectedTime);
        else sessionStorage.removeItem("selectedTime");
    }, [selectedTime]);

    useEffect(() => {
        sessionStorage.setItem("chosenServiceIds", JSON.stringify(chosenServiceIds));
    }, [chosenServiceIds]);

    useEffect(() => {
        sessionStorage.setItem("chosenServicesDurationMin", JSON.stringify(chosenServicesDurationMin));
    }, [chosenServicesDurationMin]);

    const clearBooking = () => {
        setSelectedBarberId(null);
        setSelectedDate(null);
        setSelectedTime(null);
        setChosenServiceIds([]);
        setChosenServicesDurationMin(0);
        sessionStorage.removeItem("selectedBarberId");
        sessionStorage.removeItem("selectedDate");
        sessionStorage.removeItem("selectedTime");
        sessionStorage.removeItem("chosenServiceIds");
        sessionStorage.removeItem("chosenServicesDurationMin");
    };

    return (
        <BookingDetailsContext.Provider value={{
            selectedBarberId, setSelectedBarberId,
            selectedDate, setSelectedDate,
            selectedTime, setSelectedTime,
            chosenServiceIds, setChosenServiceIds,
            chosenServicesDurationMin, setChosenServicesDurationMin,
            clearBooking,
        }}>
            {children}
        </BookingDetailsContext.Provider>
    );
};