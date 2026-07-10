import { createContext, useState, useCallback } from "react";
import Toast from "../Components/Toast/Toast";

export const ToastContext = createContext();

export const ToastProvider = ({ children }) => {
    const [toast, setToast] = useState(null);
    const showToast = useCallback((title, message, duration = 4000) => {
        setToast({title, message, duration });
        /* setToast({
        message:message,
        duration:duration}) */
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
                    title={toast.title}
                    message={toast.message}
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