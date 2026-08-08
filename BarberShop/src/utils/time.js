/* Wall-clock helpers for the "HH:mm" strings the API sends for shop hours and barber shifts (TimeOnly
   server-side, serialised as "HH:mm" by SettingsController.HoursPayload).

   Minutes-since-midnight is the form to do arithmetic in. Comparing the strings directly happens to work
   while every value is zero-padded and the same length - which is why the editors get away with it for
   ordering - but anything that SUBTRACTS times (how long is this gap, where does the next one start)
   has to be in minutes.

   Shared rather than copied: the customer slot picker and the schedule editor each had their own pair,
   and the gap finder in Schedules was about to be a third. */

export const toMin = (hhmm) => {
    const [h, m] = hhmm.split(":").map(Number);
    return h * 60 + m;
};

export const toHHMM = (min) =>
    `${String(Math.floor(min / 60)).padStart(2, "0")}:${String(min % 60).padStart(2, "0")}`;

/* The seeded shop hours, for the window before /api/Settings answers on either screen. In one place so
   the picker and the editor can't disagree about what the shop's day looks like while it loads. */
export const FALLBACK_OPEN_MIN = 9 * 60;
export const FALLBACK_CLOSE_MIN = 17 * 60 + 30;
