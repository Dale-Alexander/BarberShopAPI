import { AlertTriangle, RotateCw } from "lucide-react";
import "./ErrorState.css";

/* Inline error state for a failed page-load fetch. A toast is the wrong tool here: it vanishes after a
   few seconds and leaves the misleading empty UI ("No services yet") behind, whereas this replaces the
   content so a failed load never masquerades as "no data". onRetry wires straight to useFetch's reFetch.
   Colours inherit from the page (border/text via currentColor) so it reads on both the dark admin pages
   and the light customer booking page; only the warning icon is a fixed amber. */
const ErrorState = ({
    title = "Couldn't load this",
    message = "Something went wrong while loading. Please try again.",
    onRetry,
    inline = false,
}) => {
    return (
        <div className={`error-state${inline ? " error-state--inline" : ""}`}>
            <AlertTriangle size={inline ? 32 : 48} className="error-state__icon" />
            <h3 className="error-state__title">{title}</h3>
            <p className="error-state__message">{message}</p>
            {onRetry && (
                <button type="button" className="error-state__retry" onClick={onRetry}>
                    <RotateCw size={16} /> Retry
                </button>
            )}
        </div>
    );
};
export default ErrorState;
