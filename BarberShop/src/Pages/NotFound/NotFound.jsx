import {
    Scissors, House, ArrowRight
} from "lucide-react";
import "./NotFound.css";

const NotFound = () => {
    return(
        <div className = "nf-page" >
            <span className="nf-bg-text">404</span>
            <div className="nf-content">
                <div className="nf-logo">
                    <Scissors size={24} color="#c9a84c" />
                </div>
                <div className="nf-divider"></div>
                <h1 className="nf-title">Page Not Found</h1>
                <p className="nf-subtitle">
                    Looks like this page got a bad cut and doesn't exist anymore - or maybe it never did. Let's get you back on track.
                </p>
                <div className="nf-actions">
                    <a href="/" className="nf-btn nf-btn--dark"><House size={16} /> Back to Home</a>
                    <a href="/datetime" className="nf-btn nf-btn--gold">Book an Appointment <ArrowRight size={16}/></a>
                </div>
            </div>
        </div >
    );
}
;

export default NotFound;
