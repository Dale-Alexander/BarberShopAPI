import "./LineChart.css";
import { Line } from "react-chartjs-2";
import { Chart as ChartJS } from "chart.js/auto";
import { ArrowLeft } from "lucide-react";

/* The aggregation now happens server-side (admin-summary): `monthly` is a 12-element array of
 * { count, revenue } for the selected year, and `daily` is that month's per-day series once a month is
 * drilled into. This component just picks the count-vs-revenue field and plots it. */
const LineChart = ({ showRevenue, selectedYear, selectedMonth, setSelectedMonth, monthly, daily }) => {

    const labelsMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    const isDaily = selectedMonth !== null;
    const series = isDaily ? (daily ?? []) : (monthly ?? []);
    const values = series.map(bucket => (showRevenue ? bucket.revenue : bucket.count));
    const labels = isDaily
        ? Array.from({ length: series.length }, (_, i) => i + 1) // [1,2,3,...] days
        : labelsMonths;

    let label = "";
    if (!isDaily) {
        label = `${showRevenue ? "Revenue" : "Bookings"} (${selectedYear})`;
    }
    else {
        const monthName = labelsMonths[selectedMonth];
        label = `${showRevenue ? "Daily Revenue" : "Daily Bookings"} in ${monthName} (${selectedYear})`;
    }

    const chartData = {
        labels,
        datasets: [
            {
                label: label,
                data: values,
                borderWidth: 2,
                borderColor: "hsl(170, 70%, 45%)",
                backgroundColor: "hsla(170, 70%, 45%, 0.12)",
                tension: 0.4,
                pointRadius: 4,
                pointBorderColor: "hsl(220, 25%, 14%",
                pointBackgroundColor: "hsl(170, 70%, 45%)",
                pointBorderWidth: 2,
                fill: true
            }
        ]
    }

    const chartOptions = {
        responsive: true,
        maintainAspectRatio: false,
        plugins: {
            legend: {
                display: false},
                tooltip: {
                    backgroundColor: "hsl(220, 25%, 14%)",
                    borderColor: "hsl(220, 20%, 20%)",
                    borderWidth: 1,
                    titleColor: "hsl(210, 20%, 90%)",
                    bodyColor: "hsl(215, 15%, 55%)",
                    padding: 10,
                    callbacks: {
                        label: function (ctx) {
                            //ctx is the context object representing the data point being hovered
                            if (showRevenue) {
                                return ` €${ctx.parsed.y.toLocaleString()}`;
                            }
                            else {
                                return ` ${ctx.parsed.y} bookings`;
                            }
                        }
                    }
                }
        },
        scales: {
            x:{
            grid:{color:"hsl(220, 20%, 20%)"},
            ticks: { color: "hsl(215, 15%, 55%)", font: { size: 11 } },
            //ticks represent the values displayed along the axis
            border:{display:false}
            },
            y:{
                grid:{color: "hsl(220, 20%, 20%)"},
                ticks:{
                    color:"hsl(215, 15%, 55%)",
                    font:{size:11},
                    // precision:0 forces whole-number ticks - stops Chart.js drawing gridlines like
                    // "1.5 bookings" when the counts are small. Revenue is whole euros too.
                    precision: 0,
                    callback: function(val) {
                        return showRevenue ? `€${val.toLocaleString()}` : val;
                    }
                },
                border:{display:false}
            }
        },
        onClick: (_, elements) => {
            if (!elements.length) return;//elements represents the specific data point on the line that was clicked
            if (selectedMonth === null) {
                const monthIndex = elements[0].index;//which month on the x-axis was clicked (0-based)
                setSelectedMonth(monthIndex);
            }
        }
    }

    return (
        <>

                <button className = "graph-display-option-line" style = {{padding: selectedMonth ? "7px 14px" : "0", font:"inherit"
                 }} onClick={() => setSelectedMonth(null)}>
                {selectedMonth !== null && (
                    <p className = "back-to-year"><ArrowLeft size = {16}/> Back to yearly view</p>
                )}
                </button>

            <div className="chart-container">
                <Line key={`chart-${selectedYear ?? "all"}-${selectedMonth ?? "all"}`} data={chartData} options={chartOptions} />
            </div>

        </>
    )
}
export default LineChart;
