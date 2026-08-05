import { test, expect } from "@playwright/test";
import { BARBER_TWO, SERVICES } from "./fixtures.js";
import {
    adminApi, findBarberId, findServiceId, slotInDays,
    createPendingBooking, confirmAsCash,
} from "./helpers.js";

/* TESTS.md A8 - the same checkout open twice.
 *
 * Two real tabs in one browser context, not two contexts: a second context would be a different
 * person on a different machine, which is a different scenario and a weaker one. The customer here
 * has genuinely opened the same page twice, as people do.
 *
 * The second tab submits the REAL form rather than posting to the API, because the thing worth
 * proving is that the customer cannot produce a second payment by clicking the button in front of
 * them - and that the app tells them why instead of failing silently. */
test.describe("the same checkout open in two tabs", () => {
    let api, barberId, serviceId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberId = await findBarberId(api, BARBER_TWO.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
    });

    test.afterAll(async () => await api?.dispose());

    test("paying in one tab stops the other tab taking a second payment", async ({ context, request }) => {
        const startDateTime = slotInDays(10, 13, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });

        // Both tabs open on the same live checkout, and both actually rendered it - without this the
        // second tab's redirect later would prove nothing, since it might never have loaded at all.
        const tabOne = await context.newPage();
        const tabTwo = await context.newPage();
        await tabOne.goto(`/checkout/${publicId}`);
        await tabTwo.goto(`/checkout/${publicId}`);
        await expect(tabOne.getByText("Total")).toBeVisible();
        await expect(tabTwo.getByText("Total")).toBeVisible();

        // The customer pays in the first tab. The second tab knows nothing about it.
        await confirmAsCash(request, publicId);

        // Now they fill in and submit the second tab, which still looks perfectly live to them.
        await tabTwo.locator('input[name="customerName"]').fill("Test Customer");
        await tabTwo.getByPlaceholder("john@example.com").fill("customer@e2e.test");
        await tabTwo.locator("input[type='tel']").fill("79123456");
        await tabTwo.getByRole("button", { name: "Pay at Store" }).click();
        await tabTwo.getByRole("button", { name: "Confirm Booking" }).click();

        /* They must end up looking at the booking they already have, not at a second one and not at
           a form that silently did nothing. */
        await expect(tabTwo).toHaveURL(new RegExp(`/cancelledorcompleted/${publicId}`), { timeout: 15_000 });
        await expect(tabTwo.getByText("Transaction Complete")).toBeVisible();

        /* Exactly one BOOKING exists for the slot - the second tab did not quietly create another
           one alongside the first.

           Being precise about what this does not say: it is not a count of PAYMENTS. A second
           payment on the SAME booking would not show up here, and nothing in the admin API exposes
           payment rows to count. What actually prevents that is the unique index on
           Payment.BookingId, which is covered from the C# side. The real evidence in this spec that
           no second payment was taken is the redirect above - the customer's submit was refused
           before it reached the money. */
        const res = await api.get("/api/bookings/admin-fetch?pageSize=200");
        const { bookings } = await res.json();
        const rows = bookings.filter(
            (b) => new Date(b.startDateTime).getTime() === new Date(startDateTime).getTime()
        );
        expect(rows).toHaveLength(1);
    });
});
