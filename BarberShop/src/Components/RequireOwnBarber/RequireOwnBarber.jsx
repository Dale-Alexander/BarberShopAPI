import { useContext } from "react";
import { AuthContext } from "../../Context/AuthContext";
import { Navigate, Outlet, useParams } from "react-router-dom";

/* Sits INSIDE RequireRole on /admin/team/:id, so by the time it renders auth has already
 * resolved and `user` is guaranteed set. RequireRole only checks the ROLE is allowed, not
 * WHICH barber the :id refers to - this guard closes that gap on the frontend. The server
 * (BookingsController.BarberCanAccess) is the real boundary and already 403s a barber who
 * requests another barber's data; this just stops the app from rendering someone else's
 * page and firing a doomed request, so a barber who edits the URL is sent back to their own
 * page instead of hitting the misleading "Barber Not Found" screen. Admins may view any barber. */
const RequireOwnBarber = () => {
    const { user } = useContext(AuthContext);
    const { id } = useParams();
    if (user?.role === "BARBER" && Number(id) !== user.barberId) {
        return <Navigate to={`/admin/team/${user.barberId}`} replace />;
    }
    return <Outlet />;
};
export default RequireOwnBarber;
