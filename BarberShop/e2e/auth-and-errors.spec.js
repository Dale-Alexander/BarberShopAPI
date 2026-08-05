import { test, expect } from "@playwright/test";

/* TESTS.md E7, E8, E9 - the screens a customer or visitor lands on when something is wrong.
 *
 * No session anywhere in this file. E9 in particular is only meaningful signed out, and the other two
 * are public routes.
 *
 * These are the cheapest specs in the suite and guard the thing users forgive least: a blank screen
 * where an explanation should be. */
test.describe("dead ends and guarded doors", () => {

    /* E7, first half. No token at all - the link was mangled in an email client, or someone typed the
       path. The page must say so rather than showing a password form that can never work. */
    test("the reset screen refuses a missing token readably", async ({ page }) => {
        await page.goto("/reset-password");

        await expect(page.getByRole("heading", { name: "Invalid Link" })).toBeVisible({ timeout: 15_000 });
        await expect(page.getByText("This password reset link is missing or invalid")).toBeVisible();
        // And it must NOT offer a form that would fail on submit.
        await expect(page.locator("input[type='password']")).toHaveCount(0);
    });

    /* E7, second half. A token that LOOKS like one - expired, already used, or forged. The page can't
       know until it asks, so it shows the form and has to fail readably on submit. */
    test("the reset screen rejects a bad token with a message, not a blank screen", async ({ page }) => {
        await page.goto("/reset-password?token=not-a-real-token");

        const password = page.locator("input[type='password']");
        await expect(password.first()).toBeVisible({ timeout: 15_000 });
        await password.first().fill("NewPassw0rd!23");
        await password.nth(1).fill("NewPassw0rd!23");
        await page.getByRole("button", { name: "Confirm New Password" }).click();

        /* The toast is the whole point: without it the button just stops being busy and the customer is
           left staring at a form with no idea it failed. */
        await expect(page.getByText("Failed to reset password")).toBeVisible({ timeout: 15_000 });
        // And it must not have claimed success.
        await expect(page.getByRole("heading", { name: "Password Updated" })).toHaveCount(0);
    });

    /* E8. A booking id that doesn't exist - a mistyped or stale link. The 404 page, not a spinner that
       never resolves and not an empty shell. */
    test("an unknown booking id lands on the 404 page", async ({ page }) => {
        await page.goto("/checkout/00000000000000000000000000000000");

        await expect(page).toHaveURL(/\/404/, { timeout: 15_000 });
        await expect(page.getByRole("heading", { name: "Page Not Found" })).toBeVisible();
    });

    /* E9. A signed-out visitor typing /admin must be sent to login - and must never see the dashboard,
       even for a frame. RequireRole holds a spinner while auth resolves, which is what makes that true;
       this proves it stays true.
     *
     * A screenshot can't answer "did it flash" - it samples one moment. So a MutationObserver installed
     * before any script runs records whether an admin-only node was EVER attached, and the assertion
     * reads that record afterwards. */
    test("a signed-out visitor is sent to login without the admin page flashing", async ({ page }) => {
        await page.addInitScript(() => {
            window.__sawAdminContent = false;
            const looksAdmin = (node) =>
                node.nodeType === 1 &&
                (node.matches?.(".bookings-admin-table-btn, .sched-week, .page-title") ||
                    node.querySelector?.(".bookings-admin-table-btn, .sched-week, .page-title"));
            new MutationObserver((records) => {
                for (const r of records) {
                    for (const n of r.addedNodes) if (looksAdmin(n)) window.__sawAdminContent = true;
                }
            }).observe(document.documentElement, { childList: true, subtree: true });
        });

        await page.goto("/admin");

        await expect(page).toHaveURL(/\/login/, { timeout: 15_000 });
        expect(await page.evaluate(() => window.__sawAdminContent),
            "the admin dashboard was attached to the DOM before the redirect").toBe(false);
    });
});
