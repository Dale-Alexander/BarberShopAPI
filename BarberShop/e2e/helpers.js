import { ADMIN_STATE, appBaseUrl } from "../playwright.config.js";

/* An API client already signed in as the admin.
 *
 * The pattern every spec here should follow: ARRANGE THROUGH THE API, ASSERT THROUGH THE BROWSER.
 * Clicking through six admin screens to set up a fixture gives you five extra ways to fail for a
 * reason that has nothing to do with what you're testing - and when it breaks at 3am you can't tell
 * a real bug from a moved button.
 *
 * Separate from the `page` a customer test drives, on purpose: this carries the admin cookie, the
 * page does not. A customer page that quietly had staff rights would be testing a different
 * application - staff skip the booking lead-time buffer and see unbookable barbers.
 *
 * Reuses the session global-setup saved rather than logging in. /api/auth/login allows 10 requests a
 * minute per IP; a suite that logs in per-spec starts returning 429 partway through and the failures
 * look exactly like product bugs.
 */
export async function adminApi(playwright) {
    return await playwright.request.newContext({
        baseURL: appBaseUrl,
        storageState: ADMIN_STATE,
    });
}

/* Barber ids are database identities - they differ every time the fixture is rebuilt, so they can
   never be hard-coded. Look them up by the name the fixture created.
   Remember the roster returns FIRST names only (BarberName comes from User.Name alone). */
export async function findBarberId(api, firstName) {
    const res = await api.get("/api/Barbers/barbers-with-bookings?includeUnbookable=true");
    if (!res.ok()) throw new Error(`Could not load barbers: ${res.status()}`);

    const { barbers } = await res.json();
    const match = barbers.find((b) => b.barberName === firstName);
    if (!match) {
        throw new Error(
            `No barber called "${firstName}". Roster: ${barbers.map((b) => b.barberName).join(", ")}. ` +
            `If this is empty the fixture didn't seed - check the [e2e] lines at the top of the run.`
        );
    }
    return match.barberId;
}

/** Opens or closes a barber to new customer bookings (the Team page toggle). ADMIN only. */
export async function setAcceptingBookings(api, barberId, accepting) {
    const res = await api.patch(`/api/Barbers/${barberId}/accepting-bookings`, {
        data: { AcceptsNewBookings: accepting },
    });
    if (!res.ok()) throw new Error(`Toggle failed: ${res.status()} ${await res.text()}`);
}

/** The barber names a customer is currently offered on the booking page. */
export async function visibleBarberNames(page) {
    await page.goto("/datetime");
    await page.getByRole("heading", { name: "Choose Your Barber" }).waitFor();
    /* Wait for the roster request to have been rendered before reading the grid - otherwise an empty
       list reads as "no barbers offered" when it only means "not painted yet", and the test passes
       for entirely the wrong reason. */
    await page.locator(".bp-barber-card").first().waitFor({ state: "attached" }).catch(() => { });
    return await page.locator(".bp-barber-name").allTextContents();
}
