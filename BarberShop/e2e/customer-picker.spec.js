import { test, expect } from "@playwright/test";
import { BARBER_ONE, SERVICES } from "./fixtures.js";
import {
    adminApi, findBarberId, findServiceId, slotInDays, dateOf,
    createClosure, clearAllClosures, openSlotGrid, timeChip,
} from "./helpers.js";

/* TESTS.md E3 and E4 - what the picker refuses to offer, and why.
 *
 * Both are about the customer's own screen, so no admin session here: a page carrying staff rights
 * sees a different shop (staff skip the lead-time buffer and see unbookable barbers).
 *
 * Small day offsets on purpose - openSlotGrid can only reach dates in the month the calendar opens on.
 * Days 8 and 9 are kept clear of every other section. */
test.describe("what the customer picker will not offer", () => {
    let api, barberId, serviceId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberId = await findBarberId(api, BARBER_ONE.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
    });

    test.afterEach(async () => await clearAllClosures(api));
    test.afterAll(async () => await api?.dispose());

    /* E3. The barber works 09:00-17:30, so 20:00 is not theirs to give. Two halves, and the second is
       the one that matters: the grid not offering it is good, but anything can post to the API, so the
       server has to refuse too - and refuse in words a customer could act on, not a bare 400. */
    test("a slot outside the barber's hours is not offered, and is refused with a readable reason", async ({ page, request }) => {
        const slot = slotInDays(9, 20, 0);

        await openSlotGrid(page, { barberFirstName: BARBER_ONE.firstName, slot });
        /* The grid rendered (openSlotGrid waits for a chip), so an empty result here means "20:00 isn't
           offered", not "nothing loaded" - the distinction TESTS.md asks for. */
        await expect(timeChip(page, "20:00")).toHaveCount(0);
        await expect(timeChip(page, "11:00")).toHaveCount(1); // ...and the grid really is populated

        const res = await request.post("/api/bookings/create-pending", {
            data: { ServicesIds: [serviceId], StartDateTime: slot, BarberId: barberId },
        });
        expect(res.status()).toBe(400);

        /* Readable means it tells them what to do. A message that only says "invalid" leaves a customer
           who got here from a stale tab with nowhere to go. */
        const { message } = await res.json();
        expect(message, `unhelpful refusal: "${message}"`).toMatch(/hours|available|schedule|working/i);
    });

    /* E4. A closed day must not be clickable at all. Letting them pick it and refusing afterwards is the
       version of this that wastes the customer's time. */
    test("a day the shop is closed cannot be selected in the picker", async ({ page }) => {
        const slot = slotInDays(8, 11, 0);
        const dayNumber = String(Number(dateOf(slot).split("-")[2]));

        // Selectable to begin with - otherwise the assertion after the closure proves nothing.
        await page.goto("/datetime");
        await page.locator(".bp-barber-card", { hasText: BARBER_ONE.firstName }).click();
        const dayCell = page.locator(".bp-cal-day").filter({ hasText: new RegExp(`^${dayNumber}$`) }).first();
        await expect(dayCell).toBeEnabled();

        const closure = await createClosure(api, { date: dateOf(slot) });
        expect(closure.status(), await closure.text()).toBe(200);

        await page.goto("/datetime");
        await page.locator(".bp-barber-card", { hasText: BARBER_ONE.firstName }).click();
        const closedCell = page.locator(".bp-cal-day").filter({ hasText: new RegExp(`^${dayNumber}$`) }).first();
        await expect(closedCell).toBeDisabled();
    });
});
