import { useState, useContext, useEffect } from "react";
import { useLocation } from "react-router-dom";
import { AuthContext } from "../../../../Context/AuthContext.jsx";
import { useNavigate } from "react-router-dom";
import axios from "axios";
import { ProSidebar, Menu, MenuItem } from "react-pro-sidebar";
import { Box, Drawer, IconButton, Typography, useMediaQuery } from "@mui/material";
import { Link } from "react-router-dom";
import "react-pro-sidebar/dist/css/styles.css";
import HomeOutlinedIcon from "@mui/icons-material/HomeOutlined";
import PeopleOutlinedIcon from "@mui/icons-material/PeopleOutlined";
import ContentCutOutlinedIcon from "@mui/icons-material/ContentCutOutlined";
import ContactsOutlinedIcon from "@mui/icons-material/ContactsOutlined";
import ReceiptOutlinedIcon from "@mui/icons-material/ReceiptOutlined";
import PersonOutlinedIcon from "@mui/icons-material/PersonOutlined";
import CalendarTodayOutlinedIcon from "@mui/icons-material/CalendarTodayOutlined";
import SettingsOutlinedIcon from "@mui/icons-material/SettingsOutlined";
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

const SidebarContent = ({ collapsed, showCollapseToggle = false, isCollapsed, setIsCollapsed, selected, setSelected, handleLogout = null }) => (
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
                    onClick={showCollapseToggle ? () => setIsCollapsed(!isCollapsed) : undefined}
                    // The burger only belongs to the desktop sidebar (it collapses the rail). In the mobile
                    // drawer (showCollapseToggle=false) the floating burger owns open/close, so this header
                    // shows none of its own - that's what removes the "two burgers" case. When the desktop
                    // rail is collapsed, this icon IS the only way to expand it again.
                    icon={showCollapseToggle && collapsed ? <MenuOutlinedIcon /> : undefined}
                    style={{
                        // In the drawer, drop this header onto the fixed burger's line (~18-28px from top,
                        // see .admin-burger) so the burger reads as level with the drawer content.
                        margin: showCollapseToggle ? "10px 0 20px 0" : "20px 0 20px 0",
                        color: "var(--grey-100)",
                    }}
                >
                    {!collapsed && (
                        <Box
                            display="flex"
                            justifyContent="space-between"
                            alignItems="center"
                            // In the drawer, indent the title past the fixed burger so they don't overlap.
                            ml={showCollapseToggle ? "15px" : "52px"}
                        >
                            <Typography variant="h3" color="var(--grey-100)">
                                ADMINIS
                            </Typography>
                            {showCollapseToggle && (
                                <IconButton onClick={() => setIsCollapsed(!isCollapsed)}>
                                    <MenuOutlinedIcon />
                                </IconButton>
                            )}
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
                            title="Manage Services"
                            to="/admin/services"
                            icon={<ContentCutOutlinedIcon />}
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
                            title="Settings"
                            to="/admin/settings"
                            icon={<SettingsOutlinedIcon />}
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
    // ONE breakpoint drives everything: above it, an inline collapsible sidebar; at/below it, a single
    // floating burger + a drawer showing the full labelled menu. Dropping the second breakpoint and the
    // derived-vs-state `collapsed` split is what removes the 0/1/2-burger and stuck-drawer inconsistencies.
    const isMobile = useMediaQuery("(max-width:768px)");
    const [drawerOpen, setDrawerOpen] = useState(false);
    const [isCollapsed, setIsCollapsed] = useState(false); // desktop-only: collapse the rail to icons
    const [selected, setSelected] = useState("Dashboard");
    const { setUser } = useContext(AuthContext);
    const location = useLocation();
    const titleRoutes = {
        "/admin": "Dashboard",
        "/admin/team": "Manage Team",
        "/admin/services": "Manage Services",
        "/admin/calendar": "Calendar",
        "/admin/settings": "Settings"
    };
    useEffect(() => {
        const title = titleRoutes[location.pathname] ?? titleRoutes[location.pathname.split("/").slice(0, 3).join("/")];;
        if (title) setSelected(title);
    }, [location.pathname]);
    /* the above is used so that when the user goes directly to /admin/team without using the sidebar, the correct link on the sidebar 
    gets highlighted*/

    const navigate = useNavigate();
    const handleLogout = async () => {
        // Logout must always leave the user on /login, whatever the server says. We use plain
        // axios (NOT adminAxios) on purpose: adminAxios' interceptor turns any 401 into a
        // "Session expired" toast + hard reload, which would hijack an intentional logout if the
        // token were already stale. The backend logout is now idempotent (always 200 + clears the
        // cookie), so the catch is just a safety net for network errors - either way we clear the
        // client session and navigate in finally.
        try {
            const response = await axios.get("/api/auth/logout", { withCredentials: true });
            console.log(response.data.message);
        }
        catch (err) {
            console.log(err);
        }
        finally {
            setUser(null);
            navigate("/login", { replace: true });
        }
    }

    // Growing back to desktop must close the drawer, otherwise its open state lingers and the menu
    // reappears "already open" the next time the screen is narrow (the old resize bug).
    useEffect(() => {
        if (!isMobile) setDrawerOpen(false);
    }, [isMobile]);

    // MOBILE (<=768): one fixed burger toggles a drawer that shows the FULL labelled menu
    // (collapsed=false). The drawer carries no burger of its own; picking an item or logging out
    // closes it. This is the only branch that uses drawerOpen.
    if (isMobile) {
        return (
            <>
                <IconButton
                    onClick={() => setDrawerOpen((o) => !o)}
                    // Closed: floats over the page, aligned to the title line (see .admin-burger).
                    // Open: floats over the drawer, so shift right to line up with the menu icon column.
                    className={`admin-burger${drawerOpen ? " admin-burger--in-drawer" : ""}`}
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
                                // Paint the drawer paper the sidebar colour. Its default (white) background was
                                // showing as a strip at the very top of the drawer; matching the colour removes
                                // that seam. Header vertical alignment is handled inside the content instead.
                                backgroundColor: "var(--primary-400)",
                                "& > div": { height: "100%" }, // ensures SidebarContent box fills it
                            },
                        }
                    }}
                >
                    <SidebarContent
                        collapsed={false}
                        showCollapseToggle={false}
                        selected={selected}
                        setSelected={(val) => {
                            setSelected(val);
                            setDrawerOpen(false);
                        }}
                        handleLogout={() => {
                            setDrawerOpen(false);
                            handleLogout();
                        }}
                    />
                </Drawer>
            </>
        )
    }

    // DESKTOP (>768): inline sidebar, expanded or collapsed to an icon rail via the one header toggle.
    return (
        <SidebarContent
            collapsed={isCollapsed}
            showCollapseToggle={true}
            isCollapsed={isCollapsed}
            setIsCollapsed={setIsCollapsed}
            selected={selected}
            setSelected={setSelected}
            handleLogout={handleLogout}
        />
    )
}

export default Sidebar;