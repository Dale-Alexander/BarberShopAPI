import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync } from "node:fs";
import path from "node:path";
import { request as pwRequest } from "@playwright/test";
import { apiProject, envFile, appBaseUrl, adminEmail, adminPassword, ADMIN_STATE } from "../playwright.config.js";

/* Rebuilds the fixture before every run, so a suite never inherits the bookings, closures or
   deactivated barbers left behind by the last one. The seed itself refuses any database whose name
   doesn't contain "E2E", so a mis-pointed env file fails loudly instead of eating the dev data.

   Runs the API's own --seed-e2e command rather than talking to SQL directly: the fixture is then built
   by the same EF model the application uses, so it can't drift from the real schema. */
export default async function globalSetup() {//this runs once before any test
    if (!existsSync(envFile)) {
        throw new Error(
            `Missing ${envFile}.\n` +
            `Copy BarberShopAPI/.env.e2e.example to .env.e2e and fill it in. ` +
            `It must point at a database whose name contains "E2E".`
        );
        /* This checks the settings file exists and if not tells you exactly which file to copy. Without this youd get a confusing crash from deep inside the backend */
    }

    console.log("[e2e] seeding fixture database...");
    const output = execFileSync(
        "dotnet",
        ["run", "--no-build", "--project", apiProject, "--", "--seed-e2e"],
        {
            encoding: "utf8",
            env: { ...process.env, BARBERSHOP_ENV_FILE: envFile },
            stdio: ["ignore", "pipe", "pipe"],
        }
        /* The above runs the seed command and waits for it(Sync). Arguments are passed as an array, not one string, so paths with spaces cant break */
    );

    /* The seed reports a refusal on stdout and sets a non-zero exit code, but execFileSync only throws
       on the exit code - and a refusal that scrolled past would leave the suite running against last
       week's data and failing for reasons that look like real bugs. Check the words too. */
    if (output.includes("refused")) {
        throw new Error(`[e2e] seed refused:\n${output}`);//Belt and Braces. execFileSync already throws on a nonzero exit code. But if that check ever slipped, a 
        //refused seed would leave the tests running against the last week's data, failing in ways that look exactly like real bugs. 
    }

    const summary = output.split("\n").find((l) => l.includes("E2E fixture seeded"));
    console.log(`[e2e] ${summary?.trim() ?? "seeded"}`);

    /* Sign in ONCE and save the session cookie to disk, for every spec that needs an admin.
       POST /api/auth/login is rate limited to 10 per minute per IP (see EnableRateLimiting("login")
       in authController). A suite where each spec logs in for itself would sail past that and start
       failing with 429s partway through - failures that look like product bugs and aren't. Logging in
       here happens once per RUN no matter how many specs there are.

       Specs opt in with `test.use({ storageState: ADMIN_STATE })`. Customer-facing specs must NOT,
       or they'd be testing the shop as a logged-in admin, which is a different application. */
    mkdirSync(path.dirname(ADMIN_STATE), { recursive: true });
    const ctx = await pwRequest.newContext({ baseURL: appBaseUrl });
    const login = await ctx.post("/api/auth/login", {
        data: { Email: adminEmail, Password: adminPassword },
    });
    if (!login.ok()) {
        throw new Error(
            `[e2e] admin login failed (${login.status()}). The seed and .env.e2e disagree about ` +
            `ADMIN_EMAIL/ADMIN_PASSWORD, or the API is pointed at the wrong database.`
        );
    }
    await ctx.storageState({ path: ADMIN_STATE });
    await ctx.dispose();
    console.log(`[e2e] admin session saved`);
}
