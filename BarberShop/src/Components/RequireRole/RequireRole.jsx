import {useContext} from "react";
import { AuthContext} from "../../Context/AuthContext";
import {Navigate, Outlet, useLocation} from "react-router-dom";
import LoadingSpinner from "../LoadingSpinner/LoadingSpinner";

const RequireRole = ({ Roles }) => {
    const location = useLocation();
    const {user, loading} = useContext(AuthContext);
    if(loading){
        return(
            <LoadingSpinner message = {"Fetching user information"} color="var(--primary-500)"/>
        )
    }
    if (!user) return <Navigate to="/login" replace state={{ from: location.pathname }} />
    /* user is set only when the login is successful.
    If the login isnt successful, user wont be set
    meaning the user will be sent to the login page
    rather than continuing to the admin pages. 
    Alternatively if a user tries to access /admin directly
    RequireRole would check if the user is logged in.
    If not he would be redirected back to the login page */
    if(!Roles.includes(user.role)){
        return <Navigate to = "/admin/barber/bookings" replace/>
    }
    /* The admin can go anywhere. This if statement
    is only when a barber is already logged in 
    and tries to go back to /admin.
    RequireRole would check if the user is logg ed in 
    and would check if this user has access to /admin.
    A barber doesnt have access so he would be sent to
    /admin/barber/bookings */
    else{
        return <Outlet/>
    }
}
export default RequireRole;