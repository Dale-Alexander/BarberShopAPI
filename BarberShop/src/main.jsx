import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App.jsx'
import { ToastProvider } from './Context/ToastContext.jsx';
import { AuthContextProvider } from './Context/AuthContext.jsx';
import { BarberBookingsProvider } from './Context/BarberContext.jsx';
import "./index.css";
import { BookingDetailsContextProvider } from './Context/BookingDetailsContext.jsx';
import { BrowserRouter } from "react-router-dom";

createRoot(document.getElementById('root')).render(
    <StrictMode>
        <ToastProvider>
            <AuthContextProvider>
                <BarberBookingsProvider>
                    <BookingDetailsContextProvider>
                        <BrowserRouter>
                            <App />
                        </BrowserRouter>
                    </BookingDetailsContextProvider>
                    </BarberBookingsProvider>
            </AuthContextProvider>
        </ToastProvider>
    </StrictMode>,
)
