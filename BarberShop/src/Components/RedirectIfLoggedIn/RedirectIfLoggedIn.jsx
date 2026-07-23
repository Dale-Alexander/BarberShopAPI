import { useContext } from "react";
import { AuthContext } from "../../Context/AuthContext";
import LoadingSpinner from "../LoadingSpinner/LoadingSpinner";
import { Navigate, Outlet, useLocation } from "react-router-dom";
const RedirectIfLoggedIn = () => {
    const location = useLocation();
    const fromPath = location?.state?.from;
    const { user, loading } = useContext(AuthContext);

    // Every destination behind this guard (login/forgot/reset, or a redirect into the admin app) sits
    // on a dark background, so the loading state matches it (dark bg + light dots) to avoid a flash.
    if (loading) return <LoadingSpinner message={"Fetching user information"} color="var(--grey-100)" fullscreen />;

    if (user) {
        // Don't honor a barber-scoped `from` (/admin/team/:id) for an admin - it's usually a stale path
        // from a previous barber session, and would send the admin to that barber's page instead of the
        // dashboard (same guard as Login.jsx).
        const isBarberScoped = /^\/admin\/team\/[^/]+$/.test(fromPath || "");
        if (user.role === "ADMIN") return <Navigate to={fromPath && !isBarberScoped ? fromPath : "/admin"} replace />;
        else if (user.role === "BARBER") return <Navigate to={`/admin/team/${user.barberId}`} replace />;
    }
    // if no user, allow access
    return <Outlet />;
    /* Ok this is really interesting. The reason why we use useLocation is because lets say i am logged out and i try to access
    /admin/team directly. That will redirect me to /login. Now one i login successfully i do this inside the login component:
                        navigate(fromPath || "/admin", { replace: true });
    This didnt redirect me to /admin/team as you would expect but it redirected me to /admin. Why? Well because in the login component,
    i do setUser(). This triggers a re-render of the RedirectIfLoggedIn component and the <Navigate> used in this component will
    override the <Navigate> in the Login component.
*/
};
export default RedirectIfLoggedIn;