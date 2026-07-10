import {useState, createContext} from "react";
export const BarberBookings = createContext();

export const BarberBookingsProvider = ({children}) =>{
    const [viewBookingsBarber, setViewBookingsBarber] = useState();
    return(
        <BarberBookings.Provider value = {{viewBookingsBarber, setViewBookingsBarber}}>
            {children}
        </BarberBookings.Provider>
    )
}