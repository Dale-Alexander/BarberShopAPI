import "./StatCard.css";
const StatCard = ({ title, value, increase, icon, progress, trend }) => {
    const circumference = 2 * Math.PI * 18;
    /* Clamp to 0-100 before drawing the ring. `progress` is a percent-change string that can exceed
     * 100 (e.g. revenue more than doubled) or be non-numeric on first render - either would push
     * strokeDashoffset negative and make the ring overfill/wrap. */
    const safeProgress = Math.min(100, Math.max(0, Number(progress) || 0));
    const strokeDashoffset = circumference - (safeProgress / 100) * circumference;
    /* `trend` is "+" (up), "-" (down) or "" (no change). Colour the ring + the percentage green for a
     * rise, red for a fall, and muted when flat, so direction reads at a glance - not just from the sign. */
    const trendColor = trend === "-" ? "var(--red-500)" : trend === "+" ? "var(--green-500)" : "var(--muted-fg)";
    return (
        < div className="card-container">
            <div className="left-card-section">
                <div className="stat-card-icon">{icon}</div>
                    <p className = "stat-card-value">{value}</p>
                <p className="stat-card-title">{title}</p>
                <p className="stat-card-period">This Month</p>
            </div>
            <div className = "right-card-section">
                <svg width = "48" height = "48" className = "stat-card-progress-ring">
                    <circle 
                    cx = "24"
                    cy = "24"
                    r = "18"
                    fill = "none"
                    stroke = "var(--blue-500)"
                    strokeWidth = "3"/>
                    <circle className = "stat-card-progress"
                    cx = "24"
                    cy = "24"
                    r = "18"
                    fill = "none"
                    transform="rotate(-90 24 24)"
                    stroke = {trendColor}
                    strokeWidth = "3"
                    strokeLinecap = "round"
                    strokeDasharray={circumference}
                    strokeDashoffset={strokeDashoffset}/>
                </svg>
                <span className = "stat-card-increase" style={{ color: trendColor }}>{increase}</span>
            </div>
        </div>
    );
}
export default StatCard;