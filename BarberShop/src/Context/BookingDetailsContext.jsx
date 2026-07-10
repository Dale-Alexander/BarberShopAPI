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

    const clearBooking = () => {
        setSelectedBarberId(null);
        setSelectedDate(null);
        setSelectedTime(null);
        setChosenServiceIds([]);
        sessionStorage.removeItem("selectedBarberId");
        sessionStorage.removeItem("selectedDate");
        sessionStorage.removeItem("selectedTime");
        sessionStorage.removeItem("chosenServiceIds");
    };

    return (
        <BookingDetailsContext.Provider value={{
            selectedBarberId, setSelectedBarberId,
            selectedDate, setSelectedDate,
            selectedTime, setSelectedTime,
            chosenServiceIds, setChosenServiceIds,
            clearBooking,
        }}>
            {children}
        </BookingDetailsContext.Provider>
    );
};