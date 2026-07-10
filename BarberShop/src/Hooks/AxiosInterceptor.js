import axios from "axios";
import { useContext, useEffect } from "react";
import { ToastContext } from "../Context/ToastContext";
const adminAxios = axios.create({withCredentials:true});

const useAdminAxios = () => {
    const { showToast } = useContext(ToastContext);
    useEffect(() => {
        const interceptor = adminAxios.interceptors.response.use(res => res,
            err => {
                if (err.response?.status === 401) {
                    if (window.location.pathname !== "/login") {
                        showToast("Session expired","Please log in again");
                        window.location.href = "/login";
                    }
                }
                return Promise.reject(err);
            }
        );
        return () => adminAxios.interceptors.response.eject(interceptor);
    }, [showToast]);
};

export { adminAxios, useAdminAxios };

        /* This interceptor will be used only when it comes
        to admin functions like creating a booking.
        If the admin makes a request but his token expired,
        it will result in a 401 Unauthorized because [Authorize]
        will see that HttpContext.User is not set to anything
        and so he
        will be redirected to login */