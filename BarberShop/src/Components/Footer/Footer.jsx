import "./Footer.css";
import { Scissors } from "lucide-react";
const Footer = () => {
    return (
        <footer className="main-footer">
            <div className="main-footer-container">

                <div className="main-footer-brand">
                    <div className="main-logo-box">
                        <Scissors size={18} />
                    </div>
                    <span className="main-brand-name">The Fade House</span>
                </div>

                <p className="main-footer-hours">
                    Mon-Sat 9:00 AM - 6:00 PM | Closed Sundays
                </p>

                <div className="main-footer-contact">
                    <a href="tel:+15551234567">(555) 123-4567</a>
                    <a href="mailto:info@fadehouse.com">info@fadehouse.com</a>
                </div>
            </div>
        </footer>
    );
};
export default Footer;