/* How a barber's schedule version relates to today, and the words for it.

   Shared because two screens name the same version and must agree: the chips in the schedule editor, and
   the shop-hours conflict list on Settings that tells the admin which of those chips to go and open. If
   the two ever described a version differently, they would reintroduce exactly the confusion this text
   exists to remove - being sent to fix "barnabas" and finding the day already off, because the shift is
   on a version you weren't looking at.

   Takes anything carrying effectiveFrom / effectiveTo as "yyyy-MM-dd" strings, which is what both the
   schedules endpoint and the hours-conflict payload send (DateOnly serialises that way). */

export const todayStr = () => {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};

export const fmtDate = (iso) =>
    new Date(iso + "T00:00:00").toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });

export const versionState = (v) => {
    const t = todayStr();
    if (v.effectiveTo == null) return v.effectiveFrom > t ? "upcoming" : "active";
    if (v.effectiveTo < t) return "ended";
    return v.effectiveFrom > t ? "upcoming" : "active";
};

/* Every label leads with "Schedule" on purpose. On the editor these chips sit right under a barber's name,
   and the old "Active now" read as a statement about the BARBER (as in, currently employed) rather than
   about which set of working hours is in force. Naming the thing removes the ambiguity, and keeping the
   three labels parallel makes the row scan as one timeline. */
export const versionLabel = (v) => {
    const s = versionState(v);
    if (s === "active") return "Schedule in effect now";
    if (s === "upcoming") return `Schedule from ${fmtDate(v.effectiveFrom)}`;
    return `Schedule until ${fmtDate(v.effectiveTo)}`;
};
