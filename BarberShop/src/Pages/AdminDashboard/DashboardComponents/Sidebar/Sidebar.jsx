import { useState, useContext, useEffect } from "react";
import { useLocation } from "react-router-dom";
import { AuthContext } from "../../../../Context/AuthContext.jsx";
import { useNavigate } from "react-router-dom";
import { adminAxios } from "../../../../Hooks/AxiosInterceptor.js";
import { ProSidebar, Menu, MenuItem } from "react-pro-sidebar";
import { Box, Drawer, IconButton, Typography, useMediaQuery } from "@mui/material";
import { Link } from "react-router-dom";
import "react-pro-sidebar/dist/css/styles.css";
import HomeOutlinedIcon from "@mui/icons-material/HomeOutlined";
import PeopleOutlinedIcon from "@mui/icons-material/PeopleOutlined";
import ContactsOutlinedIcon from "@mui/icons-material/ContactsOutlined";
import ReceiptOutlinedIcon from "@mui/icons-material/ReceiptOutlined";
import PersonOutlinedIcon from "@mui/icons-material/PersonOutlined";
import CalendarTodayOutlinedIcon from "@mui/icons-material/CalendarTodayOutlined";
import HelpOutlineOutlinedIcon from "@mui/icons-material/HelpOutlineOutlined";
import MenuOutlinedIcon from "@mui/icons-material/MenuOutlined";
import LogoutIcon from '@mui/icons-material/Logout';


const Item = ({ title, to, icon, selected, setSelected, handleLogout = null }) => {
    return (
        <MenuItem
            active={selected === title}
            style={{
                color: "var(--grey-100)",
            }}
            onClick={() => {
                if (handleLogout === null) {
                    setSelected(title)
                }
                else {
                    handleLogout();
                }
            }
            }
            icon={icon}
        >
            <Typography>{title}</Typography>
            <Link to={to} />
        </MenuItem>
    );
};

const SidebarContent = ({ collapsed, isCollapsed, setIsCollapsed, selected, setSelected, handleLogout = null, isMobile = null }) => (
    <Box
        sx={{
            "& .pro-sidebar-inner": {
                background: `var(--primary-400) !important`,
            },
            "& .pro-icon-wrapper": {
                backgroundColor: "transparent !important",
            },
            "& .pro-inner-item": {
                padding: "5px 35px 5px 20px !important",
            },
            "& .pro-inner-item:hover": {
                color: "#868dfb !important",
            },
            "& .pro-menu-item.active": {
                color: "#6870fa !important",
            },
        }}
    >
        <ProSidebar collapsed={collapsed}>
            <Menu iconShape="square">
                {/* LOGO AND MENU ICON */}
                <MenuItem
                    onClick={() => setIsCollapsed(!isCollapsed)}
                    icon={isCollapsed ? <MenuOutlinedIcon /> : undefined}
                    style={{
                        margin: "10px 0 20px 0",
                        color: "var(--grey-100)",
                    }}
                >
                    {!collapsed && (
                        <Box
                            display="flex"
                            justifyContent="space-between"
                            alignItems="center"
                            ml="15px"
                        >
                            <Typography variant="h3" color="var(--grey-100)">
                                ADMINIS
                            </Typography>
                            <IconButton onClick={() => setIsCollapsed(!isCollapsed)}>
                                <MenuOutlinedIcon />
                            </IconButton>
                        </Box>
                    )}
                </MenuItem>
                <Box display="flex" flexDirection="column" height="calc(100vh - 120px)" justifyContent="space-between">

                    <Box paddingLeft={collapsed ? undefined : "10%"}>
                        {!collapsed && (
                            <Box mb="25px">
                                <Box textAlign="center">
                                    <Typography
                                        variant="h2"
                                        color="var(--grey-100)"
                                        fontWeight="bold"
                                        sx={{ m: "10px 0 0 0" }}
                                    >
                                        Ed Roh
                                    </Typography>
                                    <Typography variant="h5" color="var(--green-500)">
                                        VP Fancy Admin
                                    </Typography>
                                </Box>
                            </Box>
                        )}
                        <Item
                            title="Dashboard"
                            to="/admin"
                            icon={<HomeOutlinedIcon />}
                            selected={selected}
                            setSelected={setSelected}
                        />

                        <Typography
                            variant="h6"
                            color="var(--grey-300)"
                            sx={{ m: "15px 0 5px 20px" }}
                        >
                            Data
                        </Typography>
                        <Item
                            title="Manage Team"
                            to="/admin/team"
                            icon={<PeopleOutlinedIcon />}
                            selected={selected}
                            setSelected={setSelected}
                        />
                        <Item
                            title="Contacts Information"
                            to="/contacts"
                            icon={<ContactsOutlinedIcon />}
                            selected={selected}
                            setSelected={setSelected}
                        />
                        <Item
                            title="Invoices Balances"
                            to="/invoices"
                            icon={<ReceiptOutlinedIcon />}
                            selected={selected}
                            setSelected={setSelected}
                        />

                        <Typography
                            variant="h6"
                            color="var(--grey-300)"
                            sx={{ m: "15px 0 5px 20px" }}
                        >
                            Pages
                        </Typography>
                        <Item
                            title="Profile Form"
                            to="/form"
                            icon={<PersonOutlinedIcon />}
                            selected={selected}
                            setSelected={setSelected}
                        />
                        <Item
                            title="Calendar"
                            to="/admin/calendar"
                            icon={<CalendarTodayOutlinedIcon />}
                            selected={selected}
                            setSelected={setSelected}
                        />
                        <Item
                            title="FAQ Page"
                            to="/faq"
                            icon={<HelpOutlineOutlinedIcon />}
                            selected={selected}
                            setSelected={setSelected}
                        />
                    </Box>
                    <Box sx={{
                        "& .pro-inner-item:hover": { color: "var(--red-600) !important" }
                    }} paddingLeft={collapsed ? undefined : "10%"}>
                        <Item
                            title="Logout"
                            to="/login"
                            icon={<LogoutIcon />}
                            selected={selected}
                            setSelected={setSelected}
                            handleLogout={handleLogout}
                        />
                    </Box>
                </Box>
            </Menu>
        </ProSidebar>
    </Box>
);

const Sidebar = () => {
    const isMobile = useMediaQuery("(max-width:768px)");
    const isSmall = useMediaQuery("(max-width:530px)");
    const [drawerOpen, setDrawerOpen] = useState(false);
    const [isCollapsed, setIsCollapsed] = useState(false);
    const [selected, setSelected] = useState("Dashboard");
    const { setUser, setLoading } = useContext(AuthContext);
    const collapsed = isMobile ? true : isCollapsed;
    const location = useLocation();
    const titleRoutes = {
        "/admin": "Dashboard",
        "/admin/team": "Manage Team",
        "/admin/calendar": "Calendar"
    };
    useEffect(() => {
        const title = titleRoutes[location.pathname] ?? titleRoutes[location.pathname.split("/").slice(0, 3).join("/")];;
        if (title) setSelected(title);
    }, [location.pathname]);
    /* the above is used so that when the user goes directly to /admin/team without using the sidebar, the correct link on the sidebar 
    gets highlighted*/

    const navigate = useNavigate();
    const handleLogout = async () => {
        try {
            const response = await adminAxios.get("/api/auth/logout");
            setLoading(true);
            console.log(response.data.message);
            setUser(null);
            navigate("/login", { replace: true });
        }
        catch (err) {
            console.log(err);
        }
        finally {
            setLoading(false);
        }
    }

    useEffect(() => {
        if (isMobile) {
            setIsCollapsed(false);
        }
    }, [isMobile]);

    if (isSmall) {
        return (
            <>
                <IconButton
                    onClick={() => setDrawerOpen(!drawerOpen)}
                    sx={{
                        position: "fixed",
                        top: 10,
                        left: 17,
                        zIndex: 1300,
                        backgroundColor: "var(--primary-400)",
                        color: "var(--grey-100)",
                        "&:hover": { color: "var(--blue-600)" }
                    }}
                ><MenuOutlinedIcon />
                </IconButton>
                <Drawer
                    anchor="left"
                    open={drawerOpen}
                    onClose={() => setDrawerOpen(false)}
                    ModalProps={{ keepMounted: true }}
                    slotProps={{
                        paper: {
                            sx: {
                                height: "100%",
                                "& > div": { height: "100%" }, // ensures SidebarContent box fills it
                            },
                        }
                    }}
                >
                    <SidebarContent
                        collapsed={collapsed}
                        isCollapsed={isCollapsed}
                        setIsCollapsed={() => setDrawerOpen(false)}
                        selected={selected}
                        setSelected={(val) => {
                            setSelected(val);
                            setDrawerOpen(false);
                        }}
                        handleLogout={handleLogout}
                    />
                </Drawer>
            </>
        )
    }

    return (
        <SidebarContent
            collapsed={collapsed}
            isCollapsed={isCollapsed}
            setIsCollapsed={setIsCollapsed}
            selected={selected}
            setSelected={setSelected}
            handleLogout={handleLogout}
            isMobile={isMobile}
        />
    )
}

export default Sidebar;