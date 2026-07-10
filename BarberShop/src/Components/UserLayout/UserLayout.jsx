import { Outlet } from "react-router-dom";
import Footer from "../Footer/Footer.jsx";

const UserLayout = () => {
    return (
        <>
            <Outlet />
            <Footer />
        </>
    );
}
export default UserLayout;