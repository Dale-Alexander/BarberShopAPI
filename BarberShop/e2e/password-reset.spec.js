import { test, expect } from "@playwright/test";
import { BARBER_TWO, adminPassword } from "./fixtures.js";
import { clearEmails, waitForEmail } from "./helpers.js";

/* TESTS.md section F - the password reset, all the way through the real email.
 *
 * E7 already covers what the reset SCREEN does with a bad token. What nothing covered is the only part
 * that matters to a locked-out user: that the link they are actually sent works. The raw token exists
 * in exactly one place - that email - because only its SHA-256 hash is ever stored, so an email with a
 * broken link locks the user out permanently and no server-side test would notice.
 *
 * BARBER_TWO on purpose. Resetting a password bumps TokenVersion and kills the saved session, and
 * BARBER_TWO's is already the expendable one (C9 destroys it too - see the "Barber sessions" note in
 * TESTS.md). The password is set back to the seeded one, so a later spec logging in still works. */
test.describe("resetting a forgotten password", () => {
    test.describe.configure({ timeout: 90_000 });

    test("the emailed link actually lets you set a new password and sign in with it", async ({ page }) => {
        await clearEmails();

        // Through the real form - the endpoint is rate limited per IP, so this is the one request.
        await page.goto("/forgot-password");
        await page.getByPlaceholder("Email").fill(BARBER_TWO.email);
        await page.getByRole("button", { name: /Send|Reset|Continue/i }).first().click();

        const email = await waitForEmail({ to: BARBER_TWO.email, subject: "Reset your password" });
        expect(email.text).toContain("expires in 30 minutes");

        /* Follow the link out of the email exactly as a user would, rather than building the URL from
           a token read out of the database - the point is that what was SENT works. */
        const link = (email.text.match(/https?:\/\/\S*reset-password\?token=\S+/) || [])[0];
        expect(link, `no reset link in the email body:\n${email.text}`).toBeTruthy();

        await page.goto(link);
        await expect(page.getByRole("heading", { name: "New Password" })).toBeVisible();

        await page.getByPlaceholder("Password", { exact: true }).fill(adminPassword);
        await page.getByPlaceholder("Confirm Password").fill(adminPassword);
        await page.getByRole("button", { name: "Confirm New Password" }).click();

        await expect(page.getByRole("heading", { name: "Password Updated" })).toBeVisible({ timeout: 15_000 });

        /* And the new password genuinely works. Without this the spec would pass on a reset that
           accepted the token, said the right thing, and changed nothing. */
        await page.getByRole("button", { name: "Go to Login" }).click();
        await page.getByPlaceholder("Email").fill(BARBER_TWO.email);
        await page.getByPlaceholder("Password", { exact: true }).fill(adminPassword);
        await page.getByRole("button", { name: /Sign in|Log ?in/i }).first().click();

        await expect(page).not.toHaveURL(/\/login/, { timeout: 20_000 });
    });
});
