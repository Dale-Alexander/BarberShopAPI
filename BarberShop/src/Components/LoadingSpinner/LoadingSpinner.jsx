import { PulseLoader } from "react-spinners";
import "./LoadingSpinner.css";
/* fullscreen: covers the viewport with a solid background that matches the destination page, so the
   route guards (RequireRole/RedirectIfLoggedIn) no longer flash a white frame before the dark app or
   login screen paints. `background` overrides the default dark app colour for pages on a lighter bg. */
const LoadingSpinner = ({ message, color, inline = false, fullscreen = false, background }) => {
    const className = inline
        ? "spinner-container--inline"
        : fullscreen
            ? "spinner-container spinner-container--screen"
            : "spinner-container";
    return (
        <div className={className} style={fullscreen && background ? { background } : undefined}>
            <PulseLoader color={color} size={15} />
            <p style={{ color: color, fontSize: "13px" }}>{message}</p>
        </div>
    );
}
export default LoadingSpinner;
