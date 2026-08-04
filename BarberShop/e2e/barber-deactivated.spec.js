import { test, expect } from "@playwright/test";
import { BARBER_TWO, SERVICES, adminPassword } from "./fixtures.js";
import {
    adminApi, findBarberId, findServiceId, slotInDays,
    createPendingBooking, deactivateBarber, reviveBarber,
} from "./helpers.js";

/* TESTS.md A4 - the barber leaves while a customer is paying for them.
 *
 * Unlike a closure, deactivation DOES kill a booking that is still in checkout: it is PENDING, and
 * BookingConflictCanceller cancels PENDING conflicts outright. (Confirmed bookings survive and are
 * flagged instead - see the banner in TESTS.md.) So this is the one section-A scenario where the
 * customer's booking really does die under them.
 *
 * Uses BARBER_TWO throughout so it never fights with the specs that lean on BARBER_ONE. */
test.describe("the barber leaves while the customer is in checkout", () => {
    let api, barberId, serviceId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberId = await findBarberId(api, BARBER_TWO.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
    });

    test.afterAll(async () => {
        /* Non-negotiable. The seed runs once per RUN, so a barber left deactivated changes the shop
           for every spec after this one - including the harness tripwire that asserts the roster is
           exactly the two fixture barbers. */
        if (api) {
            await reviveBarber(api, {
                fullName: BARBER_TWO.fullName,
                email: BARBER_TWO.email,
                password: adminPassword,
            });
        }
        await api?.dispose();
    });

    test("the customer is told their barber has gone and is offered another one", async ({ page, request }) => {
        const startDateTime = slotInDays(5, 10, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });

        // Prove they were genuinely on the payment page first.
        await page.goto(`/checkout/${publicId}`);
        await expect(page.getByText("Total")).toBeVisible();

        await deactivateBarber(api, barberId);

        await page.reload();
        await expect(page).toHaveURL(new RegExp(`/cancelledorcompleted/${publicId}`));

        /* The wording has to be the BARBER one, not the generic slot-unavailable copy. Getting this
           wrong is a real failure the customer feels: "this time slot is no longer available" sends
           them off to pick another time with a barber who no longer works here. */
        await expect(page.getByText("Your Barber Is No Longer Available")).toBeVisible();
        await expect(page.getByText("This Time Slot Is No Longer Available")).toHaveCount(0);

        // And a way forward, not just an apology.
        await expect(page.getByRole("button", { name: "Choose Another Barber" })).toBeVisible();
    });
});
