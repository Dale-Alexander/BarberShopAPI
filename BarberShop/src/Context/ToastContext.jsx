import { createContext, useState, useCallback, useRef } from "react";
import Toast from "../Components/Toast/Toast";

export const ToastContext = createContext();

export const ToastProvider = ({ children }) => {
    const [toast, setToast] = useState(null);
    // Monotonic id used as the Toast's key. Retriggering while a toast is still on screen (e.g. an
    // immediate retry with the same message) would otherwise reuse the same element - its timer runs
    // in a mount-only effect and its progress bar is a mount-time animation, so neither would restart.
    // Bumping the key remounts the toast, giving every call a fresh timer + animation.
    const idRef = useRef(0);
    // type: "error" (default) | "success" | "info" -> drives the toast's colour + icon.
    const showToast = useCallback((title, message, type = "error", duration = 4000) => {
        setToast({ id: ++idRef.current, title, message, type, duration });
    }, []);
    /* useCallback returns a memoized version of 
    a function  meangin React will keep the same 
    function reference across rerenders as long as
    it its dependencies dont change*/

    const hideToast = useCallback(() => {
        setToast(null);
    }, [])

    return (
        <ToastContext.Provider value={{ showToast }}>
            {children}
            {toast && (
                <Toast
                    key={toast.id}
                    title={toast.title}
                    message={toast.message}
                    type={toast.type}
                    duration={toast.duration}
                    onClose={hideToast} />
            )
            /* When showToast is called in other components
            The toast is shown and then after 5 seconds,
            the toast disappears via hideToast.
            Message and duration are passed when showToast
            is called in other components */}
            
        </ToastContext.Provider>
    )
}