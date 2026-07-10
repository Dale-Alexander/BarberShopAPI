import Sidebar from "../../Pages/AdminDashboard/DashboardComponents/Sidebar/Sidebar";
import { Outlet } from "react-router-dom";
const AdminLayout = () => {
    return (
        <div className="admin-app">
            <Sidebar />
            <main className="home-page-main">
                <Outlet />
            </main>
        </div>
    )
}
export default AdminLayout;