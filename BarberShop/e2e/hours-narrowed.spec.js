import { test, expect } from "@playwright/test";
import { BARBER_ONE, SERVICES } from "./fixtures.js";
import {
    adminApi, findBarberId, findServiceId, slotInDays,
    createPendingBooking, getSchedule, setShifts, FULL_WEEK_SHIFTS, submitCashCheckout,
} from "./helpers.js";

/* TESTS.md A6 - the barber's hours are narrowed under a customer who is mid-payment.
 *
 * Unlike a closure, this DOES kill a live checkout: SchedulesController hands PENDING conflicts to
 * BookingConflictCanceller with CancellationReason.ScheduleChange, on all three save paths. Confirmed
 * bookings are flagged instead, same as everywhere else.
 *
 * The customer-facing wording is the SLOT one, not the barber one - the barber still works here,
 * just not then - so that is what this pins. */
test.describe("the barber's hours are narrowed while the customer is in checkout", () => {
    let api, barberId, serviceId, scheduleId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberId = await findBarberId(api, BARBER_ONE.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
        const versions = await getSchedule(api, barberId);
        scheduleId = (Array.isArray(versions) ? versions : versions.versions)[0].id;
    });

    test.afterAll(async () => {
        // Put the hours back - every later spec assumes 09:00-17:30, and a narrowed schedule would
        // silently make their slots unbookable.
        if (api && scheduleId) await setShifts(api, scheduleId, FULL_WEEK_SHIFTS);
        await api?.dispose();
    });

    test("the customer is told the slot has gone, not that the barber has", async ({ page, request }) => {
        // 16:00 is comfortably inside the seeded 09:00-17:30 and comfortably outside the 09:00-12:00
        // the hours are about to become.
        const startDateTime = slotInDays(8, 16, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });

        await page.goto(`/checkout/${publicId}`);
        await expect(page.getByText("Total")).toBeVisible();

        const narrowed = Array.from({ length: 7 }, (_, d) => ({
            DayOfWeek: d, StartTime: "09:00:00", EndTime: "12:00:00",
        }));
        const res = await setShifts(api, scheduleId, narrowed);
        expect(res.status(), await res.text()).toBe(200);

        await submitCashCheckout(page);
        await expect(page).toHaveURL(new RegExp(`/cancelledorcompleted/${publicId}`), { timeout: 15_000 });

        /* The slot wording, not the barber wording. Telling this customer their barber is no longer
           available would be false - he still works here, just not at 4pm - and would send them off
           to pick a different barber when what they need is a different time. */
        await expect(page.getByText("This Time Slot Is No Longer Available")).toBeVisible();
        await expect(page.getByText("Your Barber Is No Longer Available")).toHaveCount(0);
        await expect(page.getByRole("button", { name: "Book Another Time" })).toBeVisible();
    });
});
