import { test, expect } from "@playwright/test";
import { BARBER_ONE, BARBER_TWO, SERVICES } from "./fixtures.js";
import {
    adminApi, findBarberId, findServiceId, slotInDays,
    createPendingBooking, confirmAsCash, findBookingIdByStart, updateBooking,
} from "./helpers.js";

/* TESTS.md E5 and E6 - an admin moves a booking, and the customer's own record has to agree.
 *
 * Deliberately NOT an admin session on the page. The edit is arranged through the API; what is being
 * proved is what the CUSTOMER sees afterwards on /booking/success/:publicId, and a page holding staff
 * rights would not be that. The update emails are covered in the C# suite - this is the screen.
 *
 * Day offsets 50+ - well clear of every other section, and these never touch the calendar UI, so the
 * "same month" limit that constrains the picker specs doesn't apply. */
test.describe("an admin edits a confirmed booking", () => {
    let api, barberOneId, barberTwoId, serviceId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberOneId = await findBarberId(api, BARBER_ONE.firstName);
        barberTwoId = await findBarberId(api, BARBER_TWO.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
    });

    test.afterAll(async () => await api?.dispose());

    /* Returns both ids: the publicId is the customer's own handle on the booking (the only one in their
       URL), the integer id is what the staff endpoints take. */
    const bookAndPay = async (barberId, startDateTime, request) => {
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await confirmAsCash(request, publicId);
        return { publicId, bookingId: await findBookingIdByStart(api, startDateTime) };
    };

    /* Reads one labelled row off the customer's receipt - "Barber", "Time", "Date". */
    const receiptRow = (page, label) =>
        page.locator(".ap-receipt__row", { hasText: label }).locator(".ap-receipt__row-value");

    /* E5. Reassignment puts the appointment on someone else's chair. The customer's record naming the
       old barber is the failure that sends them to the wrong person on the day. */
    test("reassigning to another barber shows the new barber on the customer's record", async ({ page, request }) => {
        const startDateTime = slotInDays(50, 10, 0);
        const { publicId } = await bookAndPay(barberOneId, startDateTime, request);

        // It really says the original barber first - otherwise the assertion below proves nothing.
        await page.goto(`/booking/success/${publicId}`);
        await expect(receiptRow(page, "Barber")).toHaveText(BARBER_ONE.fullName, { timeout: 15_000 });

        await updateBooking(api, await findBookingIdByStart(api, startDateTime), { barberId: barberTwoId });

        await page.goto(`/booking/success/${publicId}`);
        await expect(receiptRow(page, "Barber")).toHaveText(BARBER_TWO.fullName, { timeout: 15_000 });
        await expect(page.getByText("Booking Confirmed")).toBeVisible();
    });

    /* E6. Rescheduling moves the appointment in time. Same failure mode, worse: a customer reading the
       old time turns up to an empty chair an hour out. */
    test("rescheduling shows the new time on the customer's record", async ({ page, request }) => {
        const startDateTime = slotInDays(51, 10, 0);
        const moved = slotInDays(51, 15, 0);
        const { publicId, bookingId } = await bookAndPay(barberOneId, startDateTime, request);

        await page.goto(`/booking/success/${publicId}`);
        await expect(receiptRow(page, "Time")).toContainText("10:00", { timeout: 15_000 });

        await updateBooking(api, bookingId, { startDateTime: moved });

        await page.goto(`/booking/success/${publicId}`);
        await expect(receiptRow(page, "Time")).toContainText("3:00", { timeout: 15_000 });
        // And the old time is genuinely gone, not merely joined by the new one.
        await expect(receiptRow(page, "Time")).not.toContainText("10:00");

        // The staff side agrees too - one booking, at the new time.
        const res = await api.get("/api/bookings/admin-fetch?pageSize=200");
        const { bookings } = await res.json();
        const row = bookings.find((b) => b.id === bookingId);
        expect(new Date(row.startDateTime).getHours()).toBe(15);
    });
});
