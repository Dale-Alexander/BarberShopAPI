# Scenario queue

> **The database blocker is fixed. C10–C13 have now run and passed.**
>
> `BarberShop_E2E` used to have an empty `__EFMigrationsHistory` alongside tables that already existed —
> the schema was built by `EnsureCreated`, never by migrations — so `dotnet ef database update` died on
> *"There is already an object named 'Services'"* and the API couldn't serve `/api/Barbers` against it
> (`Invalid column name 'SlotStepMin'`). Every spec in this directory was blocked, not just the new ones.
>
> It was dropped and rebuilt from migrations (39 applied). If it ever happens again, that is the fix, and
> it is safe by design: `--seed-e2e` wipes this database on every run and refuses any database whose name
> doesn't contain "E2E".
>
> **Still unmutated.** C10–C13 pass, but none has been mutation-tested, so they are not yet proven to fail
> for the right reason. Only `schedule-conflicts.spec.js` and `barber-reactivated.spec.js` have been run
> since the rebuild — the rest of the directory is unblocked but hasn't been exercised.

The work list for browser tests. **One scenario per spec, tick it off only when it passes.**

Each line is a customer-or-admin-visible outcome.

**Overlapping the C# tests is fine and often the point.** The 188 tests in `BarberShopAPI.Tests`
prove the backend does the right thing; they say nothing about what appears on screen when it does.
A scenario already covered there is still worth a spec here whenever there is a screen to look at —
"the webhook cancels and refunds the booking" is proven, "the customer staring at the checkout page
finds out" is not.

The only things that don't belong here are the ones with no UI at all: webhook signature
verification, email retry exhaustion. Those are listed at the bottom.

## Rules for anything written from this list

- **Assert on what a person sees**, not on internals. "The cancelled screen says their barber is no
  longer available" is the test. Reading `CancellationReason` out of the database is not — that's
  what the C# suite is for, and a browser test that checks it has left the browser out of the point.
- **Never assert on a spinner or an empty state as proof of success.** A test that passes because the
  page never loaded is worse than no test.
- **If a spec claims to guard a specific fix, mutation-test it**: break the fix, confirm the spec goes
  red, put the fix back. A7 claimed to guard a caching header, survived the header being removed, and
  the claim had to be withdrawn. Writing "this protects X" without checking is how a suite ends up
  full of tests nobody can rely on.
- Admin specs: `test.use({ storageState: ADMIN_STATE })`. Never log in inside a spec — the login
  endpoint is rate limited to 10/min and a suite that logs in per-spec will start throwing 429s.
- Customer specs must NOT use the admin state, or they're testing a different application.
- Set up state through the **API** (`request.post(...)`), assert through the **browser**. Clicking
  through six admin screens to arrange a fixture is five extra ways for the test to fail for the
  wrong reason.
- Every service, barber and price is in `fixtures.js`. Never hard-code them in a spec.
- The customer booking page shows barbers by **FIRST NAME ONLY** (`BarberName` is projected from
  `User.Name`). Use `BARBER_ONE.firstName`.
- If a scenario turns out not to be reachable from a browser, **write that down here and move on.**
  Do not force it, and do not weaken the assertion until it passes.

---

## A. The booking is killed while the customer is in checkout

The customer is on `/checkout/:publicId` or has just paid. Something removes the slot underneath
them. What do they actually see?

- [x] A2. A full-day shop closure lands on the slot mid-checkout → customer ends up on the cancelled
      screen with closure wording, not "Booking Confirmed". `checkout-killed.spec.js`
- [x] A3. A booking cancelled after confirmation never shows as confirmed on the success screen
      (`/booking/success/:publicId`), allowing for the poll in `Summary.jsx`. `checkout-killed.spec.js`
      **The premise was wrong and the scenario shrank.** A closure does NOT cancel an already-paid
      booking — `DatesController` leaves confirmed bookings on their slot with their money and flags
      them, with a note saying the customer has not been told. The only way a paid booking dies from
      a closure is the webhook race, which needs Stripe to reach localhost. What's tested here is the
      success screen's own behaviour.
      **The webhook race is now covered too** — `stripe listen` plus the harness's webhook gate made it
      drivable. See F2 in section F.
- [x] A4. The barber is deactivated mid-checkout → customer sees the barber-unavailable wording and a
      route back to rebook with someone else. `barber-deactivated.spec.js`
      Note: deactivation is the one section-A trigger that really does kill a live checkout, because
      the booking is still PENDING. Confirmed bookings survive and are flagged, same as a closure.
      The spec revives the barber afterwards — there is no reactivate endpoint, you re-POST
      `create-barber` with the same email and it finds the soft-deleted row.
- [x] A5. Someone else takes the same slot first → the taken time is not offered in the picker, and
      booking it anyway is refused with a readable message. `slot-taken.spec.js`
      Note: a PENDING booking already holds the slot — the roster filters on `Status != CANCELLED`,
      not "confirmed only" — so a customer mid-checkout blocks everyone else from that time.
      `openSlotGrid` / `timeChip` in `helpers.js` drive the picker; reuse them for section E.
- [x] A6. The barber's hours are narrowed so the slot falls outside them mid-checkout → the customer
      is told the SLOT has gone, not the barber. `hours-narrowed.spec.js`
      Note: this does kill a live checkout. All three schedule save paths hand PENDING conflicts to
      `BookingConflictCanceller` with `CancellationReason.ScheduleChange`. Confirmed bookings are
      flagged instead, as everywhere else.
- [x] A7. Customer pays, then hits browser Back to the checkout page → lands on the completed screen,
      not a live payment form. `pay-twice.spec.js`
      **Partly covered, and the gap is deliberate.** Mutation-tested by commenting out the
      `Cache-Control: no-store` headers on the checkout endpoint — the spec still passed, so it does
      NOT guard those headers. Playwright's `goBack` re-runs the SPA fetch either way. The status
      guard behind the redirect is covered; the caching fix is not, and would need a different
      approach (bfcache, or asserting on response headers directly).
- [x] A8. Customer has the same checkout open in two tabs and pays in one → the other tab's submit is
      refused and lands on the completed screen. `two-tabs.spec.js`
      Two real tabs in one context, and the second tab drives the actual form rather than posting to
      the API. Note the limit written into the spec: it proves no second BOOKING was created, not no
      second PAYMENT — nothing in the admin API exposes payment rows to count, and the unique index
      on `Payment.BookingId` is what guards that (C#-covered).

> **Read before writing any more of section A or B.** A closure and a schedule change do NOT cancel
> a booking that is already confirmed — they FLAG it and leave it standing, money and all, for an
> admin to deal with by hand. What the note offers depends on scope: a barber-only closure (or a
> schedule change) says reassign / move / cancel, while a shop-wide closure says only move or cancel,
> because every chair is shut and reassigning achieves nothing. Only PENDING bookings are cancelled
> outright. Any
> scenario below phrased as "X cancels the confirmed booking" is wrong about the product; the real
> outcome is a worklist entry. Barber deactivation is the same story (confirmed bookings survive and
> are flagged).

## B. The Needs-Review worklist

Every one of these ends with a row an admin has to act on. The C# suite proves the flag gets set;
these prove **the admin can find it and clear it**.

Flag sites, from `FlagForReview` call sites — `BookingCanceller`, `BookingConflictCanceller` (x2),
`BarbersController` (barber deactivated), `DatesController` (closure), `SchedulesController` (hours
narrowed), `WebHookController` (x2 — orphaned charge, closure-during-payment), `EmailService` (x4 —
each cancellation email that exhausted its retries).

All of B1–B7 live in `needs-review.spec.js`. They are **admin specs**, so `test.use({ storageState:
ADMIN_STATE })` — which applies to the `request` fixture too, not just the page. That is why the
customer half of every arrangement goes through `guestApi()`: the plain fixture is not anonymous in
this file, and `createPendingBooking` on it would silently take the staff path (no lead-time buffer).

Only a **confirmed** booking reaches the worklist. A pending one is cancelled outright and never
flagged, so every spec here pays with `confirmAsCash` first.

Counts are asserted as a **delta** against the count before, never an absolute. The suite shares one
database and a booking can be flagged with no spec asking — a cancellation email that exhausts its
Hangfire retries flags its own. `afterEach` clears flags, closures and hours so each spec starts from
a known floor.

- [x] B1. Barber deactivated with live bookings → each flagged row appears in the admin worklist with
      a reason the admin can actually read and act on. `needs-review.spec.js`
- [x] B2. Closure created over live bookings → same. `needs-review.spec.js`
- [x] B3. Hours narrowed so bookings fall outside → same, with the outside-hours wording.
      `needs-review.spec.js`
- [x] B4. The worklist count badge matches the number of flagged rows. `needs-review.spec.js`
      Note: the badge only renders while the count is above zero, so `openNeedsReviewList` waiting on
      it doubles as proof the flag landed — and fails readably when it didn't.
- [x] B5. Mark-reviewed clears the row, and the count drops. `needs-review.spec.js`
      Two bookings on purpose: clearing the only one takes the badge to zero and unmounts it, so the
      count could not be read afterwards.
- [x] B6. **Two problems on one booking** — flag it for hours, then for a closure. Both notes must be
      readable; the second must not erase the first. (This is the specific bug `BookingReview` was
      written to prevent, so it's worth a browser test.) `needs-review.spec.js`
      **Mutation-tested.** Made `FlagForReview` overwrite instead of append: the spec went red on the
      missing hours sentence, leaving only the closure note — the exact regression. Fix restored.
- [x] B7. Editing a flagged booking does NOT clear the flag, and the row is still marked when the
      admin lands back on the table. `needs-review.spec.js`
      **Mutation-tested.** Set `NeedsReview = false` in `UpdateBooking`: the spec went red waiting for
      the Needs Review button that never appeared. Fix restored.
- [ ] ~~B8. A booking flagged for a *failed refund*~~ — **not being written; see the bottom of the file.**
      No longer impossible (the app handles the async refund failure now), but what a browser would add
      over B1–B7 is the rendering of a worklist row they already cover.

## C. Excluding and re-including a barber's bookings

The two directions of the same lever: work is taken away from a barber, then given back.

C1–C5 are in `schedule-conflicts.spec.js`, C6–C7 in `accepting-bookings.spec.js`, C8–C9 in
`barber-access.spec.js`, C10–C11 back in `schedule-conflicts.spec.js`, C12–C13 in
`barber-reactivated.spec.js`.

Unlike section B, C1–C5 are **not** arranged through the API. The warning modal IS the subject — C1 and
C2 are entirely about what an admin sees *before* anything is saved — so the hours are edited in the
real editor at `/admin/schedules`.

**Open the editor with `?barberId=`, never the toolbar dropdown.** `onPickBarber` clears `versions` and
leaves the reload to an effect keyed on `barberId`, so picking the barber who is ALREADY selected (the
first in the roster, preselected on load) empties the editor and never refills it — blank week, no
spinner, no error, until the page is reloaded. `openSchedules()` takes the id and goes straight there.
**This is a real product bug, not just a test hazard.**

- [x] C1. Narrowing hours over existing bookings raises the conflict warning **before** saving, and
      names the affected bookings. `schedule-conflicts.spec.js`
      Asserts the shop is genuinely unchanged at that moment (hours still 17:30, nothing flagged) —
      otherwise "Go back" would be a lie.
- [x] C2. Cancelling at that warning changes nothing — the schedule and the bookings are untouched.
      `schedule-conflicts.spec.js`
- [x] C3. Confirming saves the schedule and flags the affected bookings. `schedule-conflicts.spec.js`
      The booking stays COMPLETED — grandfathered, never cancelled.
- [x] C4. Widening the hours back tells the admin which review notes it has just made stale.
      `schedule-conflicts.spec.js`
      **Mutation-tested.** Made `announceBackInsideHours` a no-op: the spec went red on the missing
      "These bookings fit again" modal. Also asserts the count does NOT drop — it's a list to go and
      check, not a clear-them-all button.
- [x] C5. Deleting a schedule version. `schedule-conflicts.spec.js`
      **The premise was out of date.** `DeleteVersion` is no longer a gap — it runs the same
      `FindOrphanedBookingsAsync` and returns 409 unless `confirmOrphaned`, with its own wording
      ("the hours this restores", since nothing new is being proposed). Tested as the warn-then-flag
      path it now is. **Mutation-tested:** disabling that check turned the spec red.
- [x] C6. "Accepting bookings" toggled OFF → the barber disappears from the customer picker but keeps
      their existing bookings and is still visible to staff. `accepting-bookings.spec.js`
- [x] C7. Toggled back ON → the barber returns to the customer picker. `accepting-bookings.spec.js`
- [x] C8. A barber winding down can still open and edit their own bookings (this regressed once —
      `includeUnbookable`). `barber-access.spec.js`
      **Mutation-tested.** Dropped `includeUnbookable=true` from the staff roster fetch and the spec
      went red — the barber vanished from their own edit page, exactly the original regression.
- [x] C9. Deactivating a barber signs them out (TokenVersion bumps) — prove the session actually dies.
      `barber-access.spec.js`
      Proves the session was live FIRST, or it would also pass for one that never worked.
- [x] C10. Creating a seasonal change over existing bookings warns before writing, and the create dialog
      stands down instead of stacking under the conflict. `schedule-conflicts.spec.js`
      **Passing, not yet mutation-tested.** C1–C5 cover the save and delete paths; the
      create path (`POST /api/schedules/barber/{id}`) had no coverage. Also asserts nothing was written,
      the same guarantee C1 makes. The stacking half matters because the conflict's own button re-posts
      the create with `confirmOrphaned` — two modals open would leave "Save anyway" returning to a dialog
      for a change that had just been made.
- [x] C11. A seasonal change copies the version's SAVED shifts, not unsaved edits in the editor.
      `schedule-conflicts.spec.js`
      **Passing, not yet mutation-tested.** Guards a behaviour change: the create used to post the editor's state, so a
      half-finished edit nobody had committed was baked into a new season — and once the control became a
      modal it did so with the week hidden behind the overlay. Asserts the new version carries the saved
      hour, that the original version is untouched (Create must not double as a Save), and that the modal
      says the draft won't travel. No booking involved: C10 owns the conflict, this owns which hours get
      copied.
- [x] C12. Reactivating a barber lands the admin in that barber's schedule editor.
      `barber-reactivated.spec.js`
      **Passing, not yet mutation-tested.** A revived barber keeps their surviving schedule untouched, while `UpdateShopHours`
      ignores deactivated barbers when vetoing an hours change — so they can come back with shifts
      outside the shop's hours. The editor flags them inline on open; this is what gets the admin there.
      Driven through the Team screen on purpose: `reviveBarber()` posts straight to the API and would
      skip every line under test. Asserts the editor opened on THAT barber, since the URL alone would
      pass for a page that fell back to the first in the roster.
- [x] C13. Reactivating a barber who has stranded bookings shows that list first; dismissing it performs
      the handoff. `barber-reactivated.spec.js`
      **Passing, not yet mutation-tested.** The back-on-duty list renders on the Team page, so navigating immediately would
      unmount it and lose the only place those bookings are gathered. Asserts the URL is still `/admin/team`
      while the list is up — if that ever goes green with the URL already moved, the list is rendering on a
      page that is unmounting. Also asserts nothing was cleared, as C4 does for the widening-hours twin.

### Barber sessions

C8 and C9 are the only specs that run as a BARBER — an admin cookie would pass every assertion in them
while proving nothing. `global-setup.js` saves `BARBER_ONE_STATE` and `BARBER_TWO_STATE` alongside the
admin one (barbers share the admin password, so no extra secret). Three logins per run, still well
inside the 10/min limit, and it keeps "never log in inside a spec" intact.

**Two states, not one, because C9 destroys the one it uses.** Deactivation bumps `TokenVersion` and
reviving does not put it back, so `BARBER_TWO_STATE` is dead for the rest of the run once C9 has gone.
Any future spec needing a live barber session must use BARBER_ONE.

## D. Money on screen

- [ ] D1. A €25.50 service shows as **€25.50** on the checkout total, the confirmation receipt, and
      the admin bookings table. (This was broken until recently — it read "€25.5".)
- [ ] D2. Booking with several services shows the correct summed total.
- [ ] D3. A cash booking shows the amount as owed, not as paid.
- [ ] D4. Admin edits the collected amount → the new figure shows in the table and the dashboard.

## E. Everything else worth a browser

E3–E4 are in `customer-picker.spec.js`, E5–E6 in `admin-edits.spec.js`, E7–E9 in
`auth-and-errors.spec.js`. None of them use a staff session on the page: E5 and E6 assert on the
CUSTOMER's own record, and a page holding staff rights would not be that.

- [ ] E1. The whole happy path … pay with test card `4242 4242 4242 4242`.
      **Half of this is now done, the other half is still blocked.** The card leg is covered by F1: the
      Stripe CLI plus the harness's gate made it drivable, and a real card now confirms a real booking.
      What remains blocked is the FRONT of the journey — picking services — for the reason under E2.
      F1 arranges the booking through the API and starts at `/checkout`; a spec that starts where the
      customer starts still cannot be written.
- [ ] E2. Cash booking end to end.
      **Blocked on the unbuilt Services page.** Nothing outside the "Book again" rebook path in
      `AlreadyPaid` ever calls `setChosenServiceIds`, so a fresh customer cannot choose services and
      `create-pending` rejects the booking. Three bugs found underneath this are now fixed — the
      singular `/api/booking/create-pending` 404, the `ServiceIds`/`ServicesIds` payload key, and the
      closure-date parsing in E4 — so `handleUserCreate` now sends a shape the API accepts (verified by
      probe: that exact body returns 200). What remains is only the missing data: build the Services
      page so something sets `chosenServiceIds`, and this becomes writable as specified.
- [x] E3. Booking a slot outside the barber's hours is refused, with a readable reason.
      `customer-picker.spec.js`
      Both halves: the grid doesn't offer 20:00, AND the API refuses it in words a customer can act on
      (asserted against the message, since a bare 400 would pass a status-only check).
- [x] E4. A closed day is not selectable in the picker. `customer-picker.spec.js`
      **Found and fixed a real bug.** `isDateClosed` did `new Date("yyyy-MM-dd")`, which the spec parses
      as UTC midnight — 02:00 local in Malta — while every calendar cell is local midnight. So a
      closure never matched its own FIRST day, and a single-day closure never matched at all: the shop
      was shut and the customer could still pick the day. Correct in UTC, wrong in the shop's own
      timezone, which is how it survived. Fixed with `parseLocalDay`; **mutation-tested** by putting
      `new Date` back, which turned the spec red.
- [x] E5. Admin reassigns a booking to another barber → the customer-facing record reflects the new
      barber. (The update email is C#-tested; this is the screen.) `admin-edits.spec.js`
      Note the receipt shows the barber's FULL name — unlike the picker, which is first-name only.
- [x] E6. Admin reschedules a booking → new time shows everywhere it should. `admin-edits.spec.js`
      Asserts the old time is gone, not merely joined by the new one, and that the staff table agrees.
- [x] E7. Password reset: request → the reset screen rejects a bad/expired token readably.
      `auth-and-errors.spec.js`
      Two cases: no token at all shows "Invalid Link" and no form; a token-shaped string shows the form
      and fails on submit with a toast, since the page can't know until it asks.
- [x] E8. A customer hitting an unknown booking id gets the 404 page, not a blank screen.
      `auth-and-errors.spec.js`
- [x] E9. Staff-only pages redirect a signed-out visitor to login rather than flashing the content.
      `auth-and-errors.spec.js`
      "Didn't flash" can't be answered by a screenshot, which samples one moment — a `MutationObserver`
      installed via `addInitScript` records whether an admin-only node was EVER attached, and the
      assertion reads that record after the redirect.

## F. Paying by card, and the emails

Everything above pays cash, because until `stripe listen` was wired in nothing could deliver a webhook
to localhost and a card booking never got past "Finalising your booking...". These need the harness's
sink server (`sink-server.js`) — a stand-in for Resend, and a **gate in front of the webhook** that a
spec can hold open. See the README for the one-time `stripe listen --print-secret` setup; without it
the card specs skip with the reason attached instead of failing.

**Why the gate exists.** The webhook's refund-and-cancel branches only fire in the window between
Stripe taking the money and the webhook landing — measured at **under a second** on this machine. Kill
the slot before that and `BookingConflictCanceller` voids the still-voidable PaymentIntent, so the
customer never pays and the branch is never reached; kill it after and the booking is already COMPLETED
and merely gets flagged. Holding the delivery is the only way in. The event stays genuinely Stripe's,
signed and verified as normal — only its arrival time is ours.

F1–F4 are in `card-payment.spec.js`, F5–F8 in `webhook-races.spec.js`, F9–F12 in `emails.spec.js`,
F13 in `password-reset.spec.js`.

- [x] F1. A customer pays with `4242 4242 4242 4242` → the confirmed screen, with the barber, service,
      CARD and €25.50 read row by row off the receipt. `card-payment.spec.js`
- [x] F2. A declined card (`4000 0000 0000 0002`) → Stripe's own wording on screen, still on checkout,
      Confirm live again, and no confirmation email. `card-payment.spec.js`
- [x] F3. Cancelling a card booking promises the refund — the counterpart to F11's cash booking, which
      must not. `card-payment.spec.js`
- [x] F4. `redirect_status=failed` (an abandoned Revolut Pay payment) sends them back to checkout rather
      than spinning for ten seconds. `card-payment.spec.js` — needs no forwarder, it's pure frontend.
- [x] F5. **A closure lands between the card succeeding and the webhook** → refunded, cancelled, and the
      customer sees the slot wording, never "Booking Confirmed". `webhook-races.spec.js`
      This is A3's webhook race, which used to be unreachable.
- [x] F6. The barber is deactivated in that window → the barber wording and "Choose Another Barber",
      not the slot wording. `webhook-races.spec.js`
- [x] F7. The hours are narrowed in that window → the slot wording, and an email that says the hours
      changed and specifically NOT that the shop closed. `webhook-races.spec.js`
- [x] F8. The customer settles in cash while the card payment is still in flight → the late charge is
      refunded, the appointment stands as CASH, and the email says "settle up at the shop" without
      calling it a duplicate. `webhook-races.spec.js`
- [x] F9. The confirmation email's receipt matches the screen line for line, including the 2dp total.
      `emails.spec.js`
- [x] F10. Moving a booking emails what changed — the new time AND the new barber — then restates the
      whole appointment. `emails.spec.js`
- [x] F11. A cash cancellation must NOT promise a refund. `emails.spec.js`
- [x] F12. An edit that changes nothing sends no email at all. `emails.spec.js`
- [x] F13. Password reset end to end: request it, open the link **out of the real email**, set a new
      password, sign in with it. `password-reset.spec.js`
      E7 covers what the screen does with a bad token; this covers the only thing a locked-out user
      cares about — that the link they were sent works. The raw token exists nowhere but that email
      (only its SHA-256 hash is stored), so a broken link locks the user out for good and no
      server-side test would see it.

### What section F deliberately leaves out

The pass over the webhook and the emails was meant to be exhaustive, so here is everything it does NOT
cover and why — none of it is an oversight.

**Webhook branches with no browser path.** Missing or corrupt metadata, an unknown booking id, a
redelivered event for a payment already recorded, and an event carrying something other than a
PaymentIntent. Stripe cannot be made to send any of these from a real payment — producing them means
forging an event, which is exactly what `WebhookRefundTests` already does through the front door with a
genuine signature. A browser adds nothing.

**Emails with no browser path.** The 2-hour reminder (Hangfire schedules it for two hours before the
appointment); every retry-exhaustion flag (six attempts, minutes of backoff between them); and the
refund notice's "duplicate card payment" shape, which needs two PaymentIntents to succeed against one
booking — the checkout voids the previous intent before creating another, so a browser cannot arrange
it. All three are covered in `EmailFailureReviewTests` / `WebhookRefundTests`.

### Found while writing section F

- ~~**The Confirm Booking button has two silent early returns.**~~ **Fixed, and the diagnosis was
  half wrong.** The `!isContactValid()` return in both handlers now calls `focusFirstInvalid()`, which
  scrolls the offending field into view and focuses it — the case that actually reached a customer was
  a blank name, which passes the relaxed check gating the button but fails the strict one in the
  handler. The other return, `!isConfirmValid()`, is still bare but cannot be reached by a click: the
  submit button carries the same predicate in its `disabled`, which also blocks implicit Enter
  submission. **The same silent return was still live on a third button** — `handleCardMethodSelected`,
  so clicking "Pay Online" with a blank name never opened the card form and said nothing about why —
  and is now fixed the same way.
  Note the harness's retry in `submitCheckout` is NOT explained by any of this: it fills valid contact
  details, so neither return could ever have fired for it. The swallowed click is a separate,
  still-undiagnosed race; the comment there has been corrected so nobody starts from the wrong cause.
- ~~**The success screen gives the webhook ten seconds.**~~ **Fixed.** It polled 5× at 2s and then sent
  the customer to `/checkout` — a payment form, shown to somebody Stripe had already told us paid. The
  bad case was never the common one (`Checkout.jsx` bounces a non-PENDING booking straight back out, so
  a late webhook self-healed into a blank beat), but with the webhook still in flight it really did
  offer a second payment, and that charge lands in the orphaned-charge branch as a worklist row.
  Now: backoff to about 30 seconds, then a screen that says the payment was received, that confirmation
  happens server-side without the page open, and that they can close it — with "Check again" as the only
  control and no way to pay twice. The spinner says "Payment received - finalising your booking...", so
  the money question is answered while they wait rather than after.
  Deliberately says **settled**, not confirmed: the webhook re-checks the slot, so F5–F7's endings
  (refund and cancel) are live possibilities and this screen must not promise an outcome.
  No spec covers the new screen — reaching it means holding a webhook for 30 seconds, which the harness's
  gate could do but which would add half a minute to a run to assert on a message. Not worth it.
- **A barber-unavailable email is not sent when a barber is deactivated. This is deliberate — decided,
  not outstanding.** `BarbersController` hands only PENDING bookings to `BookingConflictCanceller` and
  flags the confirmed ones, and a pending booking never gets an email, so that flow notifies nobody.
  That is the house rule and every sibling flow follows it: a closure (`DatesController`) and narrowed
  hours (`SchedulesController`) also flag confirmed bookings without emailing. It is also right on its
  own terms — the appointment still stands until an admin reassigns or cancels it, so an email here
  would be announcing a change that has not happened, which is exactly what the worklist note says
  ("The customer has NOT been told"). The email is reachable through the webhook race (F6) or a staff
  cancel of an already-flagged booking, and that is where it belongs.

---

## Not worth driving from a browser — do not attempt

Add to this list rather than fighting a scenario. Each entry must say why, and the why matters: most of
these **cannot** be driven, but an entry can also earn its place by being drivable and not worth the
spec. Say which, because "impossible" quietly becoming "we decided not to" is how a suite ends up with
gaps nobody remembers choosing.

- Stripe webhook signature failures — no browser involvement; covered in `WebhookRefundTests`.
- Email send failures and their retry-exhaustion flags — the flag IS visible (it lands in the
  Needs-Review worklist), but reaching it means six failed Hangfire attempts and the backoff between
  them is measured in MINUTES, which no browser spec can sit through. The sink can be switched to
  failing (`POST /_control/email-mode`) if this is ever worth revisiting. Covered in
  `EmailFailureReviewTests`.
- The 2-hour reminder email — scheduled by Hangfire for two hours before the appointment, so driving it
  means either waiting or reaching into Hangfire's storage. Neither belongs in a browser spec.
- **B8, "flagged for a failed refund" — no longer impossible. Deliberately not written.** This entry has
  had three different reasons in its life, and only the current one is a judgement rather than a wall.
  First it needed a card path that could reach localhost; section F built that. Then it was impossible
  for a real reason: **Stripe test mode has no synchronous refund failure** — the one refund-failure test
  card, `4000 0000 0000 5126`, fails ASYNCHRONOUSLY, coming back `succeeded` and flipping to `failed`
  later in a `charge.refund.updated` event — and every failed-refund branch here read the immediate
  response from `StripeRefunds.RefundIdempotentlyAsync`, so nothing in the app ever saw it. There was no
  behaviour to assert on.
  **That is fixed (see below), so the scenario is now producible in principle.** Two things stand between
  that and a spec:
  1. `stripe listen` runs with `--events payment_intent.succeeded` (`sink-server.js`), so refund events
     never reach the API in an e2e run. One line, if it were wanted.
  2. **Nobody has measured how long test mode takes to flip that refund to `failed`.** Seconds and a spec
     is writable; minutes and it belongs here for the same reason as email retry exhaustion. Measure it
     with `stripe listen` unfiltered before assuming either way.
  **Not worth writing even if the timing is fine.** All a browser could add is that a flagged booking
  appears on the worklist with a readable note — which B1–B7 already prove, on the same screen, through
  the same mechanism. The branch itself has five tests in `RefundFailureReviewTests`. That would be a
  slow, timing-dependent spec re-testing the rendering of a row.
  **This also surfaced a real gap, which was not a testing problem — now fixed.** Nothing in the
  application handled the async refund-failure event at all, and the reason was structural:
  `WebHookController` casts `stripeEvent.Data.Object as PaymentIntent` and returns early when that is
  null, so a refund event was discarded before the `switch` was ever reached — it did not even land in
  the "unhandled event type" log. A refund that Stripe accepted and then failed hours later left the
  booking CANCELLED, the `Payment` row marked REFUNDED, the customer told by email their money was
  coming, and nobody at the shop told otherwise.
  `HandleRefundOutcomeAsync` now runs ahead of that cast (`Refund.PaymentIntentId` → `Payment` →
  `Booking` → flag for review, with the amount, the refund id and the customer's number in the note).
  It dispatches on the Refund OBJECT rather than the event name, so the legacy `charge.refund.updated`
  this account sends and the newer `refund.failed` both land in it. Covered by five tests in
  `RefundFailureReviewTests`, **mutation-tested** by disabling the branch — the three flagging tests
  went red, the two "accept and ignore" ones stayed green, which is the right split.
  Two follow-ups deliberately left out of that change: `Payment.Status` stays `REFUNDED` (a truthful
  value needs a new enum member, which moves the three `EmailService` branches that read it), and the
  customer gets no second email — they have one promising a refund, and the correction is a phone call.
- **A1, "admin cancels a booking mid-checkout" — not a real flow.** `admin-fetch` filters to
  `Payment != null` and defaults to COMPLETED, so a PENDING booking never appears in the admin list.
  An admin cannot see a checkout in progress, so cannot cancel one. What actually kills a live
  checkout is a closure (A2), a deactivated barber (A4), losing the slot (A5), or the expiry job.
