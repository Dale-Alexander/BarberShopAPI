import { defineConfig, devices } from "@playwright/test";
import { fileURLToPath } from "node:url";
import path from "node:path";
import dotenv from "dotenv";

const here = path.dirname(fileURLToPath(import.meta.url)); 
//import.meta.url is this file's own location. fileUrlToPath converts it from a URL to a normal path, dirname strips the filename
//Example: /Users/alex/projects/app. 
/* import.meta.url: Returns the absolute file:// URL string of the current module 
(e.g., file:///Users/alex/projects/app/index.js).fileURLToPath(...):
Converts that file:// URL into a standard local system file path (e.g., /Users/alex/projects/app/index.js).path.dirname(...): Strips the filename from the end, 
leaving just the directory path (e.g., /Users/alex/projects/app) */
export const apiProject = path.resolve(here, "../BarberShopAPI/BarberShopAPI.csproj");
/* Example: /Users/developer/projects/my-dotnet-app/scripts/BarberShopAPI/BarberShopAPI.csproj */

//Why bother with the above. Because the working directory changes depending on how you launch things. Absolute paths built from the config's own position always
//land right. 
export const envFile = path.resolve(here, "../BarberShopAPI/.env.e2e");

/* Read the SAME file the API is about to read, so the specs sign in with the seeded admin without the
   password being written down in two places (or, worse, committed). */
const e2eEnv = dotenv.config({ path: envFile }).parsed ?? {};
//Reads the same .env.e2e the backend reads. a missing file gives you undefined rather than a crash because of the ?? {}.
export const adminEmail = e2eEnv.ADMIN_EMAIL;
export const adminPassword = e2eEnv.ADMIN_PASSWORD;

export const apiBaseUrl = "http://localhost:5205";
export const appBaseUrl = "http://localhost:5173";

/* The harness's own little server: it stands in for Resend so specs can read the email the customer was
   sent, and it gates Stripe's webhook so a spec can hold a delivery while it changes the shop underneath
   the customer. See e2e/sink-server.js for why both of those are needed. */
export const sinkPort = 5299;
export const sinkBaseUrl = `http://localhost:${sinkPort}`;

/* Read here, not inside the sink, so the mismatch check runs against the very file the API is about to
   load - the two can never drift apart and leave a run failing on signatures for no visible reason. */
const stripeWebhookSecret = e2eEnv.STRIPE_WEBHOOK_SECRET ?? "";

/* Where global-setup parks the signed-in admin's cookie. Specs that need staff rights do
   `test.use({ storageState: ADMIN_STATE })` instead of logging in themselves - see the rate-limit
   note in global-setup.js. Gitignored: it holds a live session token. */
export const ADMIN_STATE = path.resolve(here, "./e2e/.auth/admin.json");

/* The same trick for the two fixture barbers - staff specs that must NOT have admin rights (a barber
   may only reach his own bookings and his own schedule) need a session that is genuinely a barber's.
   Saved by global-setup for the same rate-limit reason as the admin one.

   TWO of them, not one, because C9 destroys the session it uses: deactivating a barber bumps
   TokenVersion and the middleware then rejects that cookie for the rest of the run. Reviving them does
   not put TokenVersion back. So the spec that kills a session gets its own barber. */
export const BARBER_ONE_STATE = path.resolve(here, "./e2e/.auth/barber1.json");
export const BARBER_TWO_STATE = path.resolve(here, "./e2e/.auth/barber2.json");

export default defineConfig({
    testDir: "./e2e",//where the tests live
    /* Serial, single worker. Every spec shares one database and one shop calendar, so parallel specs
       would book over each other's slots and deactivate each other's barbers. Browser tests here are
       about a handful of important journeys, not volume - correctness beats wall-clock. */
    workers: 1,
    fullyParallel: false,
    /* The above means one test at a time. Playwright normally runs many at once for speed. Here that's wrong: all tests share one database and 
    one shop calendar. 2 parallel tests would book the same 2pm slot or one would deactivate a barber the other is midway through using. Youd
    get failures that arent bugs. */
    /* No retries locally. A test that only passes on the second go is a test that has already told you
       something, and silently retrying is how a flaky suite becomes a suite nobody trusts. */
    retries: 0,
    /* when a test fails, dont quietly run it again. Auto-retry hides flakiness, and hidden flakiness is how a suite becomes something nobody trusts */
    reporter: [["list"], ["html", { open: "never" }]],
    /* "list" prints results in the terminal. html writes a browsable report. open: "never" stops it launching a browser at you - which matters overnight, 
    when nobody's there.*/
    globalSetup: "./e2e/global-setup.js",

    use: {
        baseURL: appBaseUrl,
        /* Both on first retry only would be useless with retries: 0 - keep them on failure so a run
           that dies overnight leaves behind something to look at in the morning. */
        trace: "retain-on-failure",
        screenshot: "only-on-failure",
        video: "retain-on-failure",
        /* The 3 lines of code above. Only kept when something fails, so passing runs dont fill your disk. A trace is the valuable one: a full recording you 
        can scrub through, seeing the DOM, the network callsm and the state at every step. */
    },

    projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
    /* Which browser? devices["desktop Chrome"] presets a realistic screen size and user-agent. You could add FireFox etc.  */

    /* Both servers are started by Playwright so a run is one command. --no-build on the API because
       `npm run e2e` builds once up front; letting three separate dotnet invocations each try to build
       the same project races on the output files. */
    webServer: [
        /* Started as a webServer rather than from global-setup so Playwright owns its lifetime: it comes
           up before the first spec and is killed when the run ends, taking `stripe listen` with it. Its
           health endpoint stays down until the Stripe forwarder has either reported Ready! or given up,
           so no card spec can start against a webhook that isn't being delivered yet. */
        {
            command: "node e2e/sink-server.js",
            url: `${sinkBaseUrl}/_control/health`,
            env: {
                E2E_SINK_PORT: String(sinkPort),
                E2E_API_WEBHOOK_URL: `${apiBaseUrl}/api/webhook`,
                E2E_STRIPE_WEBHOOK_SECRET: stripeWebhookSecret,
            },
            reuseExistingServer: false,
            timeout: 60_000,
            stdout: "pipe",
            stderr: "pipe",
        },
        {
            command: `dotnet run --no-build --project "${apiProject}"`,//no build because npm run e2e already bulds once. 
            url: `${apiBaseUrl}/api/Barbers/barbers-with-bookings`,
            /* url is a health check. Playwright repeatedly requests it and wont start testing until it answers. Without this, tests would start flying at a backend
            that is still booting and dail randomly. I picked this endpoint because it needs no login. */
            env: {
                BARBERSHOP_ENV_FILE: envFile,//this is the line that points the backend at the test database - it feeds from Program.cs
                ASPNETCORE_ENVIRONMENT: "Development",
                ASPNETCORE_URLS: apiBaseUrl,//pints the port so it cant drigt
                /* EF logs every statement it runs at Information. Left on, a two-test run buries its
                   own result under hundreds of lines of SQL - which matters most for an unattended run,
                   where the log is the only thing left to read afterwards.
                   The dots stay: "__" separates config LEVELS, it does not escape dots inside a logger
                   category name, so Microsoft_EntityFrameworkCore_... silently matches nothing. */
                "Logging__LogLevel__Default": "Warning",
                "Logging__LogLevel__Microsoft.EntityFrameworkCore": "Warning",

                /* The above 2 lines turn loggin from "every SQL statement" to "warnings only". Before this 2 tests produced hundreds of lines of SQL and you 
                couldnt find the result. */
            },
            /* Never reuse an API already listening on 5205. Reusing one would mean running the whole
               suite against whatever database THAT process was started with - almost certainly the
               development one - and the suite cancels bookings, creates closures and deactivates
               barbers. Failing with "port in use" is the correct outcome: stop your dev API first. */
            reuseExistingServer: false,
            //A safety rule, not a default. Normally playwright reuses a server already on that port. Here that's dangerous. If your dev API is runnin on 5205,
            //the tests would attach to it - and its connected to your real database. 
            timeout: 120_000,
            stdout: "pipe",
            stderr: "pipe",
        },
        {
            command: "npm run dev",
            url: appBaseUrl,
            reuseExistingServer: !process.env.CI,//does not allow reuse
            timeout: 120_000,
        },
    ],
});
