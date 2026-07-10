import "./LineChart.css";
import { Line } from "react-chartjs-2";
import { Chart as ChartJS } from "chart.js/auto";
import{ArrowLeft} from "lucide-react";
const LineChart = ({ showRevenue, selectedYear, selectedMonth, setSelectedMonth, data, loading }) => {

    console.log(data);

    const isLeapYear = (year) => {
        if (year % 400 === 0) return true;
        if (year % 100 === 0) return false;
        if (year % 4 === 0) return true;
        return false;
    }
    const getDaysInMonth = (selectedMonth, selectedYear, yearsInData) => {
        if (selectedMonth !== 1) {
            return new Date(2000, selectedMonth + 1, 0).getDate();
            //gives you the last Day of the current Month
        }
        if (selectedYear === "All Years") {
            const hasLeapYear = yearsInData.some(y => isLeapYear(y));//returns true or false
            return hasLeapYear ? 29 : 28;
        }
        //we do this because isLeapYear("All Years") is invalid. Also remember
        //that this is used for when we select a february
        //  and display its data across all years(i think). We need to
        //display 29 days not 28 across all years
        return isLeapYear(selectedYear) ? 29 : 28;
    }

    const monthlyCounts = new Array(12).fill(0); //[0,0,0,...]
    const yearOptions = [...new Set(data?.map(b =>
        new Date(b.startDateTime).getFullYear()
    ))].sort();


    const filteredData = selectedYear === "All Years" ? data : data?.filter(b => new Date(b.startDateTime).getFullYear() === selectedYear);
    filteredData.forEach(b => {//.forEach does nothing if length is 0
        const bookingDateMonth = new Date(b.startDateTime).getMonth();;
        if (!showRevenue) {
            monthlyCounts[bookingDateMonth]++;
        }
        else {
            monthlyCounts[bookingDateMonth] += b.amount;
        }
    })

    const labelsMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    let dailyCounts = [];
    let dailyLabels = [];
    if (selectedMonth !== null) {
        const daysInMonth = getDaysInMonth(selectedMonth, selectedYear, yearOptions);
        dailyCounts = new Array(daysInMonth).fill(0);
        dailyLabels = Array.from({ length: daysInMonth }, (_, i) => i + 1);//[1,2,3,...] 
        filteredData.forEach(b => {
            const date = new Date(b.startDateTime);
            if (date.getMonth() === selectedMonth) {
                const day = date.getDate() - 1;
                if (!showRevenue) {
                    dailyCounts[day]++;
                }
                else {
                    dailyCounts[day] += b.amount;
                }
            }
        })
    }
    let label = "";
    if (!selectedMonth) {
        //yearly view
        if (showRevenue) {
            label = `Revenue (${selectedYear === "All Years" ? "All Years" : selectedYear})`;
        }
        else {
            label = `Bookings (${selectedYear === "All Years" ? "All Years" : selectedYear})`;
        }
    }
    else {
        const monthName = labelsMonths[selectedMonth];
        if (selectedYear === "All Years") {
            label = !showRevenue
                ? `Daily Bookings in ${monthName} (All Years)`
                : `Daily Revenue in ${monthName} (All Years)`;
        } else {
            label = !showRevenue
                ? `Daily Bookings in ${monthName} (${selectedYear})`
                : `Daily Revenue in ${monthName} (${selectedYear})`;
        }
    }

    const chartData = {
        labels: selectedMonth === null ? labelsMonths : dailyLabels,
        datasets: [
            {
                label: label,
                data: selectedMonth === null ? monthlyCounts : dailyCounts,
                borderWidth: 2,
                borderColor: "hsl(170, 70%, 45%)",
                backgroundColor: "hsla(170, 70%, 45%, 0.12)",
                tension: 0.4,
                pointRadius: 4,
                pointBorderColor:"hsl(220, 25%, 14%",
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
                                return ` $${ctx.parsed.y.toLocaleString()}`;
                            }
                            else {
                                ` ${ctx.parsed.y} bookings`;
                            }
                            /* ctx.parsed.y is the y-value of the hovered point
                            So the tooltip will either show "$123456" or "12 bookings" */
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
                    callback: function(val) {
                        return showRevenue ? `$${(val / 1).toFixed(0)}` : val;
                    }
                },
                border:{display:false}
            }
        },
        onClick: (_, elements) => {
            if (!elements.length) return;//elements represents the specific data point on the line that was clicked
            if (selectedMonth === null) {
                const monthIndex = elements[0].index;//which month on the x-axis point(realistically on the graph via data points) did the user click
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