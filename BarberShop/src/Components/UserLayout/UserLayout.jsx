import { Outlet } from "react-router-dom";
import Footer from "../Footer/Footer.jsx";

const UserLayout = () => {
    return (
        // Not a fragment: .user-app is the always-mounted light surface behind every customer page,
        // so a page rendering null mid-navigation can't expose the dark app background (see index.css).
        <div className="user-app">
            <Outlet />
            <Footer />
        </div>
    );
}
export default UserLayout;