import "./StatCard.css";
const StatCard = ({ title, value, increase, icon, progress }) => {
    const circumference = 2 * Math.PI * 18;
    const strokeDashoffset = circumference - (progress / 100) * circumference;
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
                    stroke = "var(--green-500)"
                    strokeWidth = "3"
                    strokeLinecap = "round"
                    strokeDasharray={circumference}
                    strokeDashoffset={strokeDashoffset}/>
                </svg>
                <span className = "stat-card-increase">{increase}</span>
            </div>
        </div>
    );
}
export default StatCard;