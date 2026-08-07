import "./Footer.css";
import { Scissors } from "lucide-react";
import useFetch from "../../Hooks/useFetch";

const DAY_ABBR = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
/* Full names for the closed list, because the abbreviations don't pluralise: "Wed" + "days" is
 * "Weddays", and Tue/Thu/Sat are just as wrong. Every full name takes a plain "s". */
const DAY_FULL = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

/* "09:00" -> "9:00 AM". The API sends 24-hour times (they're TimeOnly); the footer has always read as
   12-hour, and that's the register a shopfront line wants. */
const to12h = (hhmm) => {
    const [h, m] = hhmm.split(":").map(Number);
    const suffix = h < 12 ? "AM" : "PM";
    const hour12 = h % 12 === 0 ? 12 : h % 12;
    return `${hour12}:${String(m).padStart(2, "0")} ${suffix}`;
};

/* The seven rows collapsed into the shortest true sentence, the way a shop actually writes its hours:
 * consecutive days that keep the same hours become one range ("Mon-Sat 9:00 AM - 6:00 PM"), and the
 * closed days are gathered onto the end ("Closed Sundays").
 *
 * Written out day by day this is seven lines in a footer that has room for one. Grouping is what makes it
 * fit, and it degrades honestly: a shop with genuinely different hours every day gets every day listed,
 * which is correct even if it's long.
 *
 * Starts at Monday rather than Sunday - the rows are keyed by System.DayOfWeek (Sunday = 0), but nobody
 * reads their opening hours starting on Sunday. */
const summarise = (hours) => {
    const order = [1, 2, 3, 4, 5, 6, 0];
    const rows = order.map((d) => hours.find((h) => h.dayOfWeek === d)).filter(Boolean);
    if (rows.length === 0) return null;

    const open = rows.filter((r) => !r.isClosed);
    const closed = rows.filter((r) => r.isClosed);

    const groups = [];
    for (const row of open) {
        const last = groups[groups.length - 1];
        // Same hours as the run so far AND the next day along, so Mon+Wed with equal hours stay separate.
        if (last && last.openTime === row.openTime && last.closeTime === row.closeTime
            && order.indexOf(row.dayOfWeek) === order.indexOf(last.end) + 1) {
            last.end = row.dayOfWeek;
        } else {
            groups.push({ start: row.dayOfWeek, end: row.dayOfWeek, openTime: row.openTime, closeTime: row.closeTime });
        }
    }

    const openText = groups.map((g) => {
        const days = g.start === g.end
            ? DAY_ABBR[g.start]
            : `${DAY_ABBR[g.start]}-${DAY_ABBR[g.end]}`;
        return `${days} ${to12h(g.openTime)} - ${to12h(g.closeTime)}`;
    });

    // "Closed Sundays" reads better than "Closed Sun". Full names here, not the abbreviations used for
    // the open ranges - see DAY_FULL.
    const closedText = closed.length > 0
        ? `Closed ${closed.map((r) => `${DAY_FULL[r.dayOfWeek]}s`).join(", ")}`
        : null;

    /* Returned as SEGMENTS, not one joined string. A shop with three different sets of hours produces a
     * line three times longer than the hardcoded one this replaced, and as a single string the browser
     * broke it wherever it ran out of room - stranding "Closed Sundays" on its own line, or splitting a
     * range down the middle. Each segment is kept whole and the wrap is forced to happen between them. */
    return [...openText, closedText].filter(Boolean);
};

const Footer = () => {
    /* Public endpoint, unauthenticated: the footer renders for signed-out visitors on every page, and it
       must not pull the staff settings blob to print one line. Renders nothing rather than a stale
       hardcoded line while it loads or if it fails - showing hours that might be wrong is worse than
       showing none, since someone could turn up to a closed shop on the strength of it. */
    const { data: shopHours } = useFetch("/api/Settings/public-hours");
    const hoursParts = shopHours ? summarise(shopHours) : null;

    return (
        <footer className="main-footer">
            <div className="main-footer-container">

                <div className="main-footer-brand">
                    <div className="main-logo-box">
                        <Scissors size={18} />
                    </div>
                    <span className="main-brand-name">The Fade House</span>
                </div>

                {hoursParts?.length > 0 && (
                    <p className="main-footer-hours">
                        {hoursParts.map((part, i) => (
                            <span className="main-footer-hours-part" key={part}>
                                {/* The separator belongs to the segment before it, so a line never
                                    starts with a stray "|" when the list wraps. Its spacing comes from
                                    CSS margins, not from spaces in the text: each segment is a flex item,
                                    and flex collapses whitespace at an item's edges - which left the bar
                                    tight against the following range and adrift from the preceding one. */}
                                {part}{i < hoursParts.length - 1 && <span className="main-footer-hours-sep" aria-hidden="true">|</span>}
                            </span>
                        ))}
                    </p>
                )}

                <div className="main-footer-contact">
                    <a href="tel:+15551234567">(555) 123-4567</a>
                    <a href="mailto:info@fadehouse.com">info@fadehouse.com</a>
                </div>
            </div>
        </footer>
    );
};
export default Footer;
