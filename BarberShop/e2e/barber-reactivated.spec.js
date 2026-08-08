import { test, expect } from "@playwright/test";
import { ADMIN_STATE } from "../playwright.config.js";
import { BARBER_TWO, SERVICES, adminPassword } from "./fixtures.js";
import {
    adminApi, guestApi, findBarberId, findServiceId, slotInDays,
    createPendingBooking, confirmAsCash, deactivateBarber, reviveBarber,
    clearAllFlags, needsReviewCount,
} from "./helpers.js";

/* TESTS.md C12-C13 - bringing a barber back, and where the admin lands afterwards.
 *
 * Deliberately NOT arranged through the API, for the same reason C1-C5 aren't: the Team screen's
 * reactivate flow IS the subject. reviveBarber() posts straight to create-barber and would skip every
 * line under test - the toast, the back-on-duty list, and the handoff into the schedule editor.
 * Deactivation, which is only the arrangement, does go through the API.
 *
 * Why the handoff matters: a revived barber keeps the schedule that survived deactivation untouched
 * (BarbersController's revive branch seeds a default only when there is NO current version), while
 * UpdateShopHours deliberately ignores deactivated barbers when it vetoes an hours change - refusing
 * over someone the schedule editor gives you no way to open would be a trap. So a barber can come back
 * with shifts that no longer fit the shop's opening hours. The editor names each offending shift inline
 * the moment it opens, which is no use to an admin who never goes there.
 *
 * BARBER_TWO throughout, so this never fights the specs that lean on BARBER_ONE. */
test.use({ storageState: ADMIN_STATE });

test.describe("bringing a barber back", () => {
    let api, guest, barberId, serviceId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        guest = await guestApi(playwright);
        barberId = await findBarberId(api, BARBER_TWO.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
    });

    /* Non-negotiable, and it runs after EVERY test rather than once: the seed runs per RUN, so a barber
       left deactivated by a spec that failed halfway changes the shop for everything after it - and the
       harness tripwire asserting the roster is exactly the two fixture barbers would fail somewhere
       else entirely. reviveBarber is a no-op when they are already active, which is the normal case
       here since the specs themselves revive through the UI. */
    test.afterEach(async () => {
        await reviveBarber(api, {
            fullName: BARBER_TWO.fullName,
            email: BARBER_TWO.email,
            password: adminPassword,
        });
        await clearAllFlags(api);
    });

    test.afterAll(async () => {
        await api?.dispose();
        await guest?.dispose();
    });

    /* Walks the Team screen the way an admin does: Inactive tab -> Reactivate -> set a password -> submit.
       Scoped to the modal rather than the page, because the submit button is icon-only and .btn-primary
       appears on several modals in this component. */
    const reactivateThroughTheUi = async (page) => {
        await page.goto("/admin/team");
        await page.getByRole("tab", { name: /Inactive/ }).click();

        /* Proves the deactivation actually landed before we act on it. Without this, a reactivate that
           silently did nothing would still leave the later assertions to fail for a confusing reason -
           and it makes the single "Reactivate Barber" match below unambiguous. */
        const card = page.getByRole("button", { name: "Reactivate Barber" });
        await expect(card).toHaveCount(1);
        await card.click();

        const modal = page.locator(".modal-content", { hasText: "Reactivate Barber" });
        await expect(modal).toBeVisible();
        await modal.locator('input[type="password"]').fill(adminPassword);
        await modal.locator(".modal-footer .btn-primary").click();
    };

    /* C12. The plain case: nothing was stranded, so the admin goes straight to the schedule editor with
       that barber already selected - the same handoff adding a barber has always made. */
    test("reactivating a barber lands the admin in that barber's schedule editor", async ({ page }) => {
        await deactivateBarber(api, barberId);
        await reactivateThroughTheUi(page);

        await expect(page.getByText("Barber reactivated")).toBeVisible({ timeout: 15_000 });
        await expect(page).toHaveURL(new RegExp(`/admin/schedules\\?barberId=${barberId}`), { timeout: 15_000 });

        /* The URL alone would pass for a page that landed on the wrong barber - the editor falls back to
           the first in the roster when the id doesn't resolve. Assert the editor really opened on THEM,
           and that the week rendered rather than sitting on a spinner. */
        await expect(page.locator(".sched-toolbar select")).toHaveValue(String(barberId));
        await expect(page.locator(".sched-day")).toHaveCount(7);
    });

    /* C13. The case with something to read first. Deactivating flags the barber's confirmed bookings;
       reviving hands back that list, and it renders on the Team page. Navigating immediately would
       unmount it and lose the only place those bookings are gathered together - so the list comes first
       and DISMISSING it is what performs the handoff. */
    test("a barber with stranded bookings shows the list first, and dismissing it goes to their schedule", async ({ page }) => {
        const startDateTime = slotInDays(38, 11, 0);
        const { publicId } = await createPendingBooking(guest, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await confirmAsCash(guest, publicId);

        // Their departure flags that confirmed booking - which is what comes back as the list below.
        await deactivateBarber(api, barberId);
        expect(await needsReviewCount(api)).toBeGreaterThan(0);

        await reactivateThroughTheUi(page);

        const backOnDuty = page.locator(".modal-content", { hasText: "These bookings are fine again" });
        await expect(backOnDuty).toBeVisible({ timeout: 15_000 });

        /* Still on Team at this point. If this ever goes green while the URL has already moved, the
           list is being rendered on a page that is unmounting and the admin cannot read it. */
        await expect(page).toHaveURL(/\/admin\/team/);

        await backOnDuty.getByRole("button", { name: "Got it" }).click();

        // Dismissing completes the handoff rather than just closing a modal.
        await expect(page).toHaveURL(new RegExp(`/admin/schedules\\?barberId=${barberId}`), { timeout: 15_000 });
        await expect(page.locator(".sched-toolbar select")).toHaveValue(String(barberId));

        /* And the list really was information only - nothing was cleared on the admin's behalf, which is
           the same guarantee C4 makes for the widening-hours version of this modal. */
        expect(await needsReviewCount(api)).toBeGreaterThan(0);
    });
});
