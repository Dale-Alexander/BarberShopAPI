import { test, expect } from "@playwright/test";
import { ADMIN_STATE } from "../playwright.config.js";
import { BARBER_ONE, BARBER_TWO, SERVICES } from "./fixtures.js";
import {
    adminApi, guestApi, findBarberId, findServiceId, slotInDays, dayNameOf,
    createPendingBooking, confirmAsCash, findBookingIdByStart,
    getSchedule, setShifts, FULL_WEEK_SHIFTS,
    createScheduleVersion, deleteScheduleVersion,
    openSchedules, setDayEndTime, clearAllFlags, needsReviewCount,
} from "./helpers.js";

/* TESTS.md section C1-C5 - taking work away from a barber, and giving it back.
 *
 * Unlike section B, these are NOT arranged through the API. The warning modal IS the thing under test:
 * C1 and C2 are entirely about what an admin sees BEFORE anything is saved, and a spec that PUT the
 * schedule directly would skip the only screen that matters. So the hours are edited in the real
 * editor at /admin/schedules, the way an admin edits them.
 *
 * Day offsets run 30+ to stay clear of sections A, B and E.
 *
 * Every spec restores 09:00-17:30 and clears the worklist. The suite is serial on one database and the
 * seed runs once per RUN - a narrowed schedule left behind makes later specs' slots unbookable, and
 * the failure surfaces somewhere else entirely. */
test.use({ storageState: ADMIN_STATE });

test.describe("narrowing and restoring a barber's hours", () => {
    let api, guest, barberId, serviceId, scheduleId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        guest = await guestApi(playwright);
        barberId = await findBarberId(api, BARBER_ONE.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
    });

    /* Re-read every time: C5 deletes a version, so the id from beforeAll would be stale afterwards. */
    test.beforeEach(async () => {
        const versions = await getSchedule(api, barberId);
        const list = Array.isArray(versions) ? versions : versions.versions;
        scheduleId = list[list.length - 1].id;
    });

    test.afterEach(async () => {
        /* Any extra version C5 created is gone by the time this runs (it deletes it), but a failed run
           can leave one behind - drop back to a single open-ended version before restoring the hours. */
        const versions = await getSchedule(api, barberId);
        const list = Array.isArray(versions) ? versions : versions.versions;
        for (const v of list.slice(1)) await deleteScheduleVersion(api, v.id, { confirmOrphaned: true });
        const remaining = await getSchedule(api, barberId);
        const first = (Array.isArray(remaining) ? remaining : remaining.versions)[0];
        await setShifts(api, first.id, FULL_WEEK_SHIFTS);
        await clearAllFlags(api);
    });

    test.afterAll(async () => {
        await api?.dispose();
        await guest?.dispose();
    });

    const bookAndPay = async (startDateTime) => {
        const { publicId } = await createPendingBooking(guest, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await confirmAsCash(guest, publicId);
        return await findBookingIdByStart(api, startDateTime);
    };

    /* Regression guard, found while writing this file. The toolbar dropdown preselects the first barber
       in the roster, so picking THAT barber was a no-op for barberId - which left onPickBarber's clears
       to empty the editor with nothing to refill it. Blank week, no spinner, no error, until reload.

       Both halves matter: re-picking the same barber must not break the editor, and switching to a
       different one must still actually switch it. A guard that fixed the first by breaking the second
       would pass a test that only checked one. */
    test("re-picking the barber already shown keeps the editor, and switching still switches", async ({ page }) => {
        await openSchedules(page, barberId);
        await expect(page.locator(".sched-day")).toHaveCount(7);

        await page.locator(".sched-toolbar select").selectOption({ label: BARBER_ONE.fullName });
        await expect(page.locator(".sched-day")).toHaveCount(7);
        await expect(page.locator(".sched-day input[type='time']").first()).toBeVisible();

        await page.locator(".sched-toolbar select").selectOption({ label: BARBER_TWO.fullName });
        await expect(page.locator(".sched-day input[type='time']").first()).toBeVisible({ timeout: 15_000 });
        await expect(page.locator(".sched-day")).toHaveCount(7);
    });

    /* C1. The warning has to arrive BEFORE the save, and it has to name the bookings - "3 bookings are
       affected" is not something an admin can act on. They need to know who, and when, to decide
       whether to go ahead at all. */
    test("narrowing hours over an existing booking warns before saving, and names the booking", async ({ page }) => {
        const startDateTime = slotInDays(30, 16, 0); // inside 09:00-17:30, outside the 12:00 below
        const bookingId = await bookAndPay(startDateTime);

        await openSchedules(page, barberId);
        await setDayEndTime(page, dayNameOf(startDateTime), "12:00");
        await page.getByRole("button", { name: "Save changes" }).click();

        const modal = page.locator(".sched-orphan-modal");
        await expect(modal.getByRole("heading", { name: "Bookings outside the new hours" })).toBeVisible();
        await expect(modal).toContainText("They won't be cancelled");

        // The booking itself, not just a count - date and time as the admin needs to read them.
        const entry = modal.locator(".sched-orphan-list li", { hasText: "4:00 PM" });
        await expect(entry).toBeVisible();
        await expect(entry).toContainText("Test Customer");

        /* And nothing has happened yet. The whole point of a pre-save warning is that the shop is
           unchanged while the admin decides - if the hours were already written, "Go back" would be a
           lie. Checked through the API because the screen cannot show what wasn't saved. */
        expect(await needsReviewCount(api)).toBe(0);
        const versions = await getSchedule(api, barberId);
        const shifts = (Array.isArray(versions) ? versions : versions.versions)[0].shifts;
        expect(shifts.every((s) => s.endTime.startsWith("17:30"))).toBe(true);
        expect(bookingId).toBeTruthy();
    });

    /* C2. Backing out has to change NOTHING. An admin who reconsiders and finds the hours half-applied
       has been given the worst of both. */
    test("backing out of the warning leaves the schedule and the booking untouched", async ({ page }) => {
        const startDateTime = slotInDays(31, 16, 0);
        const bookingId = await bookAndPay(startDateTime);

        await openSchedules(page, barberId);
        await setDayEndTime(page, dayNameOf(startDateTime), "12:00");
        await page.getByRole("button", { name: "Save changes" }).click();
        await expect(page.locator(".sched-orphan-modal")).toBeVisible();

        await page.getByRole("button", { name: "Go back" }).click();
        await expect(page.locator(".sched-orphan-modal")).toHaveCount(0);

        // Hours unchanged, booking unflagged, and it is still bookable time as far as the shop knows.
        const versions = await getSchedule(api, barberId);
        const shifts = (Array.isArray(versions) ? versions : versions.versions)[0].shifts;
        expect(shifts.every((s) => s.endTime.startsWith("17:30"))).toBe(true);
        expect(await needsReviewCount(api)).toBe(0);

        // And the booking is still COMPLETED - not quietly cancelled by a half-run save.
        const res = await api.get("/api/bookings/admin-fetch?pageSize=200");
        const { bookings } = await res.json();
        expect(bookings.find((b) => b.id === bookingId).status).toBe("COMPLETED");
    });

    /* C3. Going ahead saves the hours AND puts the stranded booking in the worklist. Saving without
       flagging would be the dangerous half: the admin has been warned once, the modal is gone, and
       nothing remains to tell them someone is still booked into hours that no longer exist. */
    test("confirming the warning saves the hours and flags the stranded booking", async ({ page }) => {
        const startDateTime = slotInDays(32, 16, 0);
        const bookingId = await bookAndPay(startDateTime);

        await openSchedules(page, barberId);
        await setDayEndTime(page, dayNameOf(startDateTime), "12:00");
        await page.getByRole("button", { name: "Save changes" }).click();
        await page.getByRole("button", { name: "Save anyway" }).click();

        await expect(page.getByText("Schedule saved")).toBeVisible({ timeout: 15_000 });

        // The hours really moved...
        const versions = await getSchedule(api, barberId);
        const shifts = (Array.isArray(versions) ? versions : versions.versions)[0].shifts;
        const thatDay = shifts.find((s) => s.dayOfWeek === new Date(startDateTime).getDay());
        expect(thatDay.endTime.startsWith("12:00")).toBe(true);

        // ...and the booking survived, flagged rather than cancelled.
        const res = await api.get("/api/bookings/admin-fetch?needsReview=true&pageSize=200");
        const { bookings } = await res.json();
        const flagged = bookings.find((b) => b.id === bookingId);
        expect(flagged, "the stranded booking should be in the worklist").toBeTruthy();
        expect(flagged.status).toBe("COMPLETED");
        expect(flagged.reviewReason).toContain("falls outside their schedule");
    });

    /* C4. The other direction. Widening the hours back does NOT clear the notes - deliberately, see
       BookingReview - so the admin has to be told which ones it just made stale, or they are left with
       a worklist they cannot tell is out of date. */
    test("widening the hours back names the review notes it has just made stale", async ({ page }) => {
        const startDateTime = slotInDays(33, 16, 0);
        const bookingId = await bookAndPay(startDateTime);
        const dayName = dayNameOf(startDateTime);

        // Strand it first, through the API - the narrowing is C3's subject, not this one's.
        const narrowed = FULL_WEEK_SHIFTS.map((s) =>
            s.DayOfWeek === new Date(startDateTime).getDay() ? { ...s, EndTime: "12:00:00" } : s);
        expect((await setShifts(api, scheduleId, narrowed)).status()).toBe(200);
        expect(await needsReviewCount(api)).toBe(1);

        // Now put the hours back through the editor.
        await openSchedules(page, barberId);
        await setDayEndTime(page, dayName, "17:30");
        await page.getByRole("button", { name: "Save changes" }).click();

        const modal = page.locator(".sched-orphan-modal");
        await expect(modal.getByRole("heading", { name: "These bookings fit again" })).toBeVisible({ timeout: 15_000 });
        await expect(modal.locator(".sched-orphan-list li")).toContainText(`#${bookingId}`);
        /* The instruction matters as much as the list: nothing was cleared, and the admin has to read
           each note in case it says something the hours can't answer. */
        await expect(modal).toContainText("Nothing has been cleared for you");

        // Proof it really didn't clear anything.
        expect(await needsReviewCount(api)).toBe(1);
    });

    /* C5. Removing a version snaps the PRIOR version's hours back, which strands bookings taken under
       the wider ones. TESTS.md recorded this path as having no orphan check - it has one now, and the
       wording is its own ("the hours this restores", not "the new hours") because nothing new is being
       proposed. */
    test("removing a schedule version warns about bookings outside the hours it restores", async ({ page }) => {
        // A later version with LATE hours, and a booking inside them that the 17:30 original won't cover.
        const from = slotInDays(34, 0, 0).split("T")[0];
        const late = FULL_WEEK_SHIFTS.map((s) => ({ ...s, EndTime: "20:00:00" }));
        const created = await createScheduleVersion(api, barberId, { effectiveFrom: from, shifts: late });
        expect(created.status(), await created.text()).toBe(200);

        const startDateTime = slotInDays(35, 19, 0); // inside 20:00, outside the restored 17:30
        const bookingId = await bookAndPay(startDateTime);

        const versions = await getSchedule(api, barberId);
        const list = Array.isArray(versions) ? versions : versions.versions;
        const current = list[list.length - 1];

        await openSchedules(page, barberId);
        await page.locator(".sched-version-chip").last().click();
        /* By its exact name, not /Remove/: every shift row carries a "Remove shift" icon button whose
           title becomes its accessible name, so a loose match hits one of those first and quietly edits
           the week instead of deleting the version. */
        await page.getByRole("button", { name: "Remove this schedule" }).click();

        const modal = page.locator(".sched-orphan-modal");
        await expect(modal.getByRole("heading", { name: "Bookings outside the restored hours" })).toBeVisible({ timeout: 15_000 });
        await expect(modal).toContainText("the hours this restores");
        await expect(modal.locator(".sched-orphan-list li", { hasText: "7:00 PM" })).toBeVisible();

        // Confirm, and the booking lands in the worklist rather than being cancelled.
        await page.getByRole("button", { name: "Remove anyway" }).click();
        await expect(page.getByText("Schedule removed")).toBeVisible({ timeout: 15_000 });

        const res = await api.get("/api/bookings/admin-fetch?needsReview=true&pageSize=200");
        const { bookings } = await res.json();
        const flagged = bookings.find((b) => b.id === bookingId);
        expect(flagged, "the stranded booking should be in the worklist").toBeTruthy();
        expect(flagged.status).toBe("COMPLETED");
        expect(current.id).toBeTruthy();
    });
});
