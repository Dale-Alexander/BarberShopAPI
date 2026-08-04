# Browser tests

Playwright drives a real Chromium against the real API and a real database. Everything else in the
project is already covered by the 188 xUnit tests in `BarberShopAPI.Tests`, which exercise the same
endpoints far faster and without a browser - so **only put a test here if it needs a screen.**

"What does the customer actually see when their booking is cancelled mid-checkout" needs a screen.
"Does the webhook set NeedsReview" does not; that belongs in the C# suite.

## Running

```bash
cd BarberShop
npm run e2e          # headless
npm run e2e:headed   # watch it happen
npm run e2e:ui       # pick and step through tests
npm run e2e:report   # open the last HTML report
```

Stop your development API first. Port 5205 must be free — see below.

## First-time setup

1. Create the database once: `CREATE DATABASE [BarberShop_E2E]`
2. `cp ../BarberShopAPI/.env.e2e.example ../BarberShopAPI/.env.e2e` and fill it in. It is gitignored.
3. `npx playwright install chromium`

## How it hangs together

`npm run e2e` builds the API, then Playwright starts both servers and runs `global-setup.js`, which
rebuilds the fixture by calling the API's own `--seed-e2e` command.

Three things stop a run from touching your development data, and all three are deliberate:

- The API is launched with `BARBERSHOP_ENV_FILE` pointing at `.env.e2e`. Without it, `DotNetEnv`
  overwrites environment variables from `.env` — setting `DefaultConnection` on the command line does
  not work, which is why the file is chosen rather than the variable.
- `--seed-e2e` refuses any database whose name doesn't contain `E2E`. Its first act is to delete every
  row, so it will not take the caller's word for it.
- `reuseExistingServer: false` on the API. If something is already on 5205 the run fails instead of
  quietly testing against whatever database that process was started with.

## Fixture

Rebuilt before every run, defined in `BarberShopAPI/Seed/SeedE2E.cs` and mirrored in `fixtures.js`:

- Admin `admin@e2e.test` (password in `.env.e2e`)
- Barbers **Luke Camilleri** and **Mark Bugeja**, working 09:00–17:30 every day
- Services **Skin Fade** €25.50 / 30min, **Beard Trim** €12.00 / 20min, **Cut and Beard** €35.00 / 45min

Barbers share the admin password, so signing in as one needs no extra secret.

**Careful:** `/api/Barbers/barbers-with-bookings` projects `BarberName` from `User.Name` alone, so the
customer booking page shows barbers by **first name only** — "Luke", not "Luke Camilleri". Use
`BARBER_ONE.firstName` for anything customer-facing. `fixtures.js` carries both.

## Writing tests

Specs run **serially on one worker**. They share a database and a shop calendar, so parallel specs
would book over each other's slots and deactivate each other's barbers.

Retries are off. A test that only passes the second time has already told you something.
