import Sidebar from "../../Pages/AdminDashboard/DashboardComponents/Sidebar/Sidebar";
import { Outlet } from "react-router-dom";
const AdminLayout = () => {
    return (
        <div className="admin-app">
            <Sidebar />
            <main className="home-page-main">
                {/* Shared spacing wrapper so every admin page (Dashboard, Team, Services, Calendar,
                    barber bookings) sits with identical padding/max-width/centering - see .admin-page. */}
                <div className="admin-page">
                    <Outlet />
                </div>
            </main>
        </div>
    )
}
export default AdminLayout;