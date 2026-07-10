import { PulseLoader } from "react-spinners";
import "./LoadingSpinner.css";
const LoadingSpinner = ({ message, color, inline = false }) => {
    return (
        <div className={inline ? "spinner-container--inline" : "spinner-container"}>
            <PulseLoader color={color} size={15} />
            <p style={{ color: color, fontSize: "13px" }}>{message}</p>
        </div>
    );
}
export default LoadingSpinner;