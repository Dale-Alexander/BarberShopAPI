
import {Route, Routes } from "react-router-dom";
import Home from "./Pages/Home/Home";
import Login from "./Pages/Login/Login";
import AdminDashboard from "./Pages/AdminDashboard/AdminDashboard";
import RequireRole from "./Components/RequireRole/RequireRole";
import RequireOwnBarber from "./Components/RequireOwnBarber/RequireOwnBarber";
import RedirectIfLoggedIn from "./Components/RedirectIfLoggedIn/RedirectIfLoggedIn";
import ForgotPassword from "./Pages/ForgotPassword/ForgotPassword.jsx";
import ResetPassword from "./Pages/ResetPassword/ResetPassword.jsx";
import { useAdminAxios } from "./Hooks/AxiosInterceptor.js";
import AdminCalendar from "./Pages/AdminDashboard/Calendar/Calendar.jsx";
import AdminLayout from "./Components/AdminLayout/AdminLayout.jsx";
import TeamMembers from "./Pages/AdminDashboard/TeamMembers/TeamMembers.jsx";
import Services from "./Pages/AdminDashboard/Services/Services.jsx";
import Settings from "./Pages/AdminDashboard/Settings/Settings.jsx";
import Schedules from "./Pages/AdminDashboard/Schedules/Schedules.jsx";
import BarberBookings from "./Pages/BarberBookings/BarberBookings";
import BarberDateAndTime from "./Pages/BarberDateAndTime/BarberDateAndTime.jsx";
import UserLayout from "./Components/UserLayout/UserLayout.jsx";
import Checkout from "./Pages/Checkout/Checkout.jsx";
import NotFound from "./Pages/NotFound/NotFound";
import AlreadyPaid from "./Pages/AlreadyPaid/AlreadyPaid";
import BookingConfirmation from "./Pages/Summary/Summary";
function App() {
    useAdminAxios() // sets up interceptor with access to context
    /* App is rendered inside the ToastProvider, so when App
    renders and calls useAdminAxios(), the hook runs useContext(ToastContext),
    which looks up the component tree, finds ToastProvider and 
    pulls showToast out of it. Then it attaches showToast function to
    the interceptor. If app was outside of ToastProvider, useContext would return
    nothing and it wouldnt work. the component calling the hook must always be
    a child of the provider its trying to access */

    return (
            <Routes>
                <Route element={<UserLayout/> }>
                    <Route path="/" element={<Home />} />
                    <Route element={<RequireRole Roles = {["ADMIN", "BARBER"]} spinnerBg="var(--background)" spinnerColor="var(--primary-500)"/>}>
                        <Route path="/datetime/:bookingId" element={<BarberDateAndTime />} />
                    </Route>
                    <Route path="/checkout/:bookingId" element={<Checkout />} />
                    <Route path="/datetime" element={<BarberDateAndTime />} />
                    <Route path="/cancelledorcompleted/:bookingId" element={<AlreadyPaid />} />
                    <Route path="/booking/success/:bookingId" element={<BookingConfirmation/>}/>
                    </Route>
                <Route element={<RedirectIfLoggedIn />}>
                    <Route path="/login" element={<Login />} />
                    <Route path="/forgot-password" element={<ForgotPassword />} />
                    <Route path="/reset-password" element={<ResetPassword />} />
                </Route>
                <Route element={<RequireRole Roles={["ADMIN"]} />}>
                    <Route element={<AdminLayout />}>
                        <Route path="/admin" element={<AdminDashboard />} />
                        <Route path="/admin/team" element={<TeamMembers />} />
                        <Route path="/admin/services" element={<Services />} />
                        <Route path="/admin/settings" element={<Settings />} />
                    </Route>
                </Route>
                <Route element={<RequireRole Roles={["ADMIN", "BARBER"]} />}>
                    <Route element={<AdminLayout />}>
                        {/* Both of these are open to barbers as READ-ONLY views of their own working life:
                            the Calendar shows the closures that affect them (their own time off plus
                            shop-wide ones) and Schedules shows the shifts they've been given. Creating,
                            editing and removing either is the admin's, so the pages render without those
                            controls for a barber and the backend refuses the writes regardless. */}
                        <Route path="/admin/calendar" element={<AdminCalendar />} />
                        <Route path="/admin/schedules" element={<Schedules />} />
                        <Route element={<RequireOwnBarber />}>
                            <Route path="/admin/team/:id" element={<BarberBookings/> }/>
                        </Route>
                    </Route>
                </Route>
                <Route path="*" element={<NotFound />} />
            </Routes>
    )
}

export default App
