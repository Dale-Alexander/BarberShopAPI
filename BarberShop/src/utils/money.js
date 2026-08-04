// Every euro amount the app displays goes through here. Money is stored as decimal(6,2) server-side
// but arrives as a raw JSON number, so 25.50 comes back as 25.5 and rendering it directly gave totals
// that read "€25.5" - on a receipt and a checkout total, which is where it looks worst.
//
// Grouping is on so a four-figure dashboard total reads "€12,340.50" rather than "€12340.50"; booking
// and service amounts are capped well below 1000 so it never kicks in there.
//
// Returns null (not "€0.00") when there's nothing to show, so callers keep their own em-dash/hidden-row
// handling for a booking with no amount on file rather than claiming it was free.
export const formatEuro = (value) => {
    if (value == null || value === "" || Number.isNaN(Number(value))) return null;
    return `€${Number(value).toLocaleString(undefined, {
        minimumFractionDigits: 2,
        maximumFractionDigits: 2
    })}`;
};
