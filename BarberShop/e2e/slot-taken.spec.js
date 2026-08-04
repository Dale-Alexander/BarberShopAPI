import { test, expect } from "@playwright/test";
import { BARBER_ONE, SERVICES } from "./fixtures.js";
import {
    adminApi, findBarberId, findServiceId, slotInDays,
    createPendingBooking, openSlotGrid, timeChip,
} from "./helpers.js";

/* TESTS.md A5 - the slot goes to somebody else.
 *
 * Worth knowing before reading this: a PENDING booking already holds the slot. The roster query
 * filters on Status != CANCELLED, not "confirmed only", so a customer part-way through checkout
 * blocks everyone else from that time. That is the behaviour being pinned here - it is what stops
 * two people paying for the same chair. */
test.describe("a slot already taken by someone else", () => {
    let api, barberId, serviceId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberId = await findBarberId(api, BARBER_ONE.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
    });

    test.afterAll(async () => await api?.dispose());

    test("the taken time is not offered to the next customer, and its neighbour still is", async ({ page, request }) => {
        const taken = slotInDays(6, 11, 0);
        await createPendingBooking(request, { barberId, serviceIds: [serviceId], startDateTime: taken });

        await openSlotGrid(page, { barberFirstName: BARBER_ONE.firstName, slot: taken });

        await expect(timeChip(page, "11:00")).toBeDisabled();
        /* A neighbouring slot MUST still be bookable. Without this the test would also pass on a day
           that failed to load any availability at all, or on a barber who was accidentally closed -
           both of which are worse bugs than the one under test. */
        await expect(timeChip(page, "12:00")).toBeEnabled();
    });

    test("booking it anyway is refused with a message that tells the customer what to do", async ({ request }) => {
        /* The screen above stops an honest customer. This is the other half: a stale page, a second
           tab, or two people submitting at the same instant. The unique index on
           (BarberId, StartDateTime) is what actually prevents the double booking, and the endpoint
           has to turn that into a sentence rather than a 500. */
        const taken = slotInDays(7, 11, 0);
        await createPendingBooking(request, { barberId, serviceIds: [serviceId], startDateTime: taken });

        const second = await request.post("/api/bookings/create-pending", {
            data: { ServicesIds: [serviceId], StartDateTime: taken, BarberId: barberId },
        });

        expect(second.ok()).toBe(false);
        const body = await second.json();
        expect(body.message).toMatch(/just booked by someone else|overlaps with an existing booking/i);
        // Never a raw server error - that would reach the customer as "something went wrong".
        expect(second.status()).toBe(400);
    });
});
