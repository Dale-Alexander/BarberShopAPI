import { test, expect } from "@playwright/test";
import { ADMIN_STATE } from "../playwright.config.js";
import { BARBER_ONE, BARBER_TWO, SERVICES, adminPassword } from "./fixtures.js";
import {
    adminApi, guestApi, findBarberId, findServiceId, slotInDays, dateOf,
    createPendingBooking, confirmAsCash, findBookingIdByStart,
    createClosure, clearAllClosures,
    deactivateBarber, reviveBarber,
    getSchedule, setShifts, FULL_WEEK_SHIFTS,
    updateBooking, needsReviewCount, clearAllFlags,
    openNeedsReviewList, reviewReasonFor,
} from "./helpers.js";

/* TESTS.md section B - the Needs Review worklist.
 *
 * The C# suite proves the flag gets SET. What it cannot answer is whether an admin can find the row,
 * read why it's there, and get it off the list - which is the entire purpose of the feature. A flag
 * nobody can act on is the same as no flag.
 *
 * These are admin specs, so the page carries the staff session. Note what that costs: test.use
 * storageState applies to the `request` fixture too, so the "customer" side of every arrangement here
 * goes through `guest` (see guestApi) - the plain fixture would take the staff booking path, which
 * skips the lead-time buffer and would be arranging bookings no customer could make.
 *
 * Day offsets start at 20 to stay clear of sections A and E, which work in the first fortnight. Every
 * spec here leaves the shop as it found it: flags cleared, closures switched off, hours restored,
 * barbers revived. The suite is serial on one database - a closure left standing shuts the shop for
 * every spec that follows, and the failure lands somewhere else entirely. */
test.use({ storageState: ADMIN_STATE });

test.describe("the needs-review worklist", () => {
    let api, guest, barberOneId, barberTwoId, serviceId, scheduleId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        guest = await guestApi(playwright);
        barberOneId = await findBarberId(api, BARBER_ONE.firstName);
        barberTwoId = await findBarberId(api, BARBER_TWO.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
        const versions = await getSchedule(api, barberOneId);
        scheduleId = (Array.isArray(versions) ? versions : versions.versions)[0].id;
    });

    /* After EVERY test, not just at the end: these specs assert on badge numbers, so each one has to
       start from the same floor. Ordered so nothing depends on the previous cleanup step. */
    test.afterEach(async () => {
        await clearAllClosures(api);
        await setShifts(api, scheduleId, FULL_WEEK_SHIFTS);
        await clearAllFlags(api);
    });

    test.afterAll(async () => {
        await api?.dispose();
        await guest?.dispose();
    });

    /* Books a slot and pays for it in cash, which is what makes it COMPLETED - and a confirmed booking
       is the only kind that gets FLAGGED. A pending one is cancelled outright instead and never
       reaches the worklist, so every spec in this section has to pay first. */
    const bookAndPay = async (barberId, startDateTime) => {
        const { publicId } = await createPendingBooking(guest, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await confirmAsCash(guest, publicId);
        return await findBookingIdByStart(api, startDateTime);
    };

    /* B1. The barber walks out with confirmed work on the books. Deactivation does NOT cancel those -
       they keep their slot, their barber and their money - so the worklist is the ONLY place this
       surfaces, and the note is the only thing telling the admin the customer is still expecting to
       be seen. */
    test("a barber who leaves puts their live bookings in the worklist with a reason the admin can act on", async ({ page }) => {
        const startDateTime = slotInDays(20, 10, 0);
        const bookingId = await bookAndPay(barberTwoId, startDateTime);

        await deactivateBarber(api, barberTwoId);
        try {
            await openNeedsReviewList(page);

            const reason = reviewReasonFor(page, bookingId);
            await expect(reason).toContainText(`${BARBER_TWO.fullName} has left the shop, and this booking is still live`);
            /* The half that costs money if it's missing: nothing has been sent to this customer, so an
               admin who assumes they've been told leaves someone turning up to no barber. */
            await expect(reason).toContainText("The customer has NOT been told");
            await expect(reason).toContainText("Reassign it to another barber or cancel it");
        } finally {
            // Not in afterEach: only this spec deactivates, and leaving it dead breaks the whole suite.
            await reviveBarber(api, {
                fullName: BARBER_TWO.fullName, email: BARBER_TWO.email, password: adminPassword,
            });
        }
    });

    /* B2. Same shape, different trigger. A closure leaves a confirmed booking standing too. */
    test("a closure over a paid booking puts it in the worklist with the closure wording", async ({ page }) => {
        const startDateTime = slotInDays(21, 10, 0);
        const bookingId = await bookAndPay(barberOneId, startDateTime);

        const closure = await createClosure(api, { date: dateOf(startDateTime) });
        expect(closure.status(), await closure.text()).toBe(200);

        await openNeedsReviewList(page);

        const reason = reviewReasonFor(page, bookingId);
        await expect(reason).toContainText("The shop is closed for this booking's slot");
        await expect(reason).toContainText("The customer has NOT been told");
    });

    /* B3. The hours move under a booking that is already paid for. The wording has to be the SLOT one
       - the barber still works here, just not then - because it decides whether the admin goes looking
       for another barber or another time. */
    test("hours narrowed under a paid booking flags it with the outside-hours wording", async ({ page }) => {
        const startDateTime = slotInDays(22, 16, 0); // inside 09:00-17:30, outside the 09:00-12:00 below
        const bookingId = await bookAndPay(barberOneId, startDateTime);

        const narrowed = Array.from({ length: 7 }, (_, d) => ({
            DayOfWeek: d, StartTime: "09:00:00", EndTime: "12:00:00",
        }));
        const res = await setShifts(api, scheduleId, narrowed);
        expect(res.status(), await res.text()).toBe(200);

        await openNeedsReviewList(page);

        const reason = reviewReasonFor(page, bookingId);
        await expect(reason).toContainText("The barber's working hours changed and this booking now falls outside their schedule");
        await expect(reason).toContainText("honour it, reschedule it, or cancel it");
    });

    /* B4. The badge is the only thing telling an admin there is work waiting at all - every other view
       hides it behind a filter. If it disagrees with the list, they either chase a row that isn't
       there or never look.

       Asserted as a DELTA against the count before, never an absolute: the suite shares one database
       and a booking can be flagged without any spec asking - a cancellation email that exhausts its
       Hangfire retries flags its own. */
    test("the badge count matches the number of flagged rows", async ({ page }) => {
        const before = await needsReviewCount(api);

        const first = await bookAndPay(barberOneId, slotInDays(23, 10, 0));
        const second = await bookAndPay(barberOneId, slotInDays(23, 11, 0));

        const closure = await createClosure(api, { date: dateOf(slotInDays(23, 10, 0)) });
        expect(closure.status(), await closure.text()).toBe(200);
        expect(await needsReviewCount(api)).toBe(before + 2);

        await openNeedsReviewList(page);

        // The badge itself, read off the screen rather than the API.
        await expect(page.locator(".review-badge")).toHaveText(String(before + 2));
        // And it agrees with what's actually listed underneath it.
        await expect(page.locator("tr.needs-review-reason-row")).toHaveCount(before + 2);
        await expect(reviewReasonFor(page, first)).toBeVisible();
        await expect(reviewReasonFor(page, second)).toBeVisible();
    });

    /* B5. The way OUT of the worklist. Two bookings on purpose: clearing the only one would take the
       badge to zero and unmount the button, so the count could not be read afterwards - and "the
       number went down" is the half of this that matters. */
    test("marking a booking reviewed clears its row and drops the count", async ({ page }) => {
        const before = await needsReviewCount(api);
        const cleared = await bookAndPay(barberOneId, slotInDays(24, 10, 0));
        const kept = await bookAndPay(barberOneId, slotInDays(24, 11, 0));

        const closure = await createClosure(api, { date: dateOf(slotInDays(24, 10, 0)) });
        expect(closure.status(), await closure.text()).toBe(200);

        await openNeedsReviewList(page);
        await expect(page.locator(".review-badge")).toHaveText(String(before + 2));

        // The admin's actual route: row menu -> Mark reviewed -> confirm the dialog.
        await page.locator(`tr:has(.booking-id-cell:text-is("${cleared}")) .three-dots-btn`).click();
        await page.getByRole("button", { name: "Mark reviewed" }).click();
        await page.locator(".review-confirm-go").click();

        await expect(reviewReasonFor(page, cleared)).toHaveCount(0);
        await expect(page.locator(".review-badge")).toHaveText(String(before + 1));
        // The other one is untouched - marking is per row, not a "clear the list" button.
        await expect(reviewReasonFor(page, kept)).toBeVisible();
    });

    /* B6. The bug BookingReview was written to prevent. Every flag site used to assign ReviewReason
       directly, so a second problem silently erased the first - a booking carrying a money reminder
       that then fell outside the hours ended up saying only that the hours changed.
       Both notes have to survive, and the admin has to be able to READ both. */
    test("a booking with two problems keeps both notes - the second does not erase the first", async ({ page }) => {
        const startDateTime = slotInDays(25, 16, 0);
        const bookingId = await bookAndPay(barberOneId, startDateTime);

        // First problem: the hours move out from under it.
        const narrowed = Array.from({ length: 7 }, (_, d) => ({
            DayOfWeek: d, StartTime: "09:00:00", EndTime: "12:00:00",
        }));
        expect((await setShifts(api, scheduleId, narrowed)).status()).toBe(200);

        // Second problem, on the same booking: the shop closes that day too.
        const closure = await createClosure(api, { date: dateOf(startDateTime) });
        expect(closure.status(), await closure.text()).toBe(200);

        await openNeedsReviewList(page);

        const reason = reviewReasonFor(page, bookingId);
        await expect(reason).toContainText("The barber's working hours changed and this booking now falls outside their schedule");
        await expect(reason).toContainText("The shop is closed for this booking's slot");
    });

    /* B7. Editing is not sign-off. Moving the booking somewhere valid fixes the CLASH but says nothing
       about whether the customer was told, so the flag deliberately survives (see the note in
       UpdateBooking) - mark-reviewed is the only way out. An admin who edits and walks away has to
       still find the row waiting. */
    test("editing a flagged booking does not clear the flag", async ({ page }) => {
        const startDateTime = slotInDays(26, 10, 0);
        const bookingId = await bookAndPay(barberOneId, startDateTime);

        const closure = await createClosure(api, { date: dateOf(startDateTime) });
        expect(closure.status(), await closure.text()).toBe(200);

        // Move it to a clear day - the conflict is gone, the flag must not be.
        await updateBooking(api, bookingId, { startDateTime: slotInDays(27, 10, 0) });

        await openNeedsReviewList(page);
        await expect(reviewReasonFor(page, bookingId)).toBeVisible();
        await expect(reviewReasonFor(page, bookingId)).toContainText("The shop is closed for this booking's slot");
    });
});
