# Scenario queue

The work list for browser tests. **One scenario per spec, tick it off only when it passes.**

Each line is a customer-or-admin-visible outcome. If a line can be proved without a browser, it does
not belong here — the 188 tests in `BarberShopAPI.Tests` already cover the backend, and duplicating
them in Playwright buys nothing but a slower, flakier suite.

## Rules for anything written from this list

- **Assert on what a person sees**, not on internals. "The cancelled screen says their barber is no
  longer available" is a test. "CancellationReason is BarberUnavailable" is not — that's already
  covered in C#.
- **Never assert on a spinner or an empty state as proof of success.** A test that passes because the
  page never loaded is worse than no test.
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

- [ ] A1. Admin cancels the booking while the customer sits on the checkout page. Customer submits →
      sees a clear refusal, not a silent failure or an infinite spinner.
- [ ] A2. A full-day shop closure lands on the slot mid-checkout → customer ends up on the cancelled
      screen with closure wording, not "Booking Confirmed".
- [ ] A3. A closure lands **after** the card succeeded (the webhook refund path) → `/summary` must
      not show "Booking Confirmed"; it should land on the cancelled screen. `Summary.jsx` polls for
      this - the test needs to allow for that delay rather than assert instantly.
- [ ] A4. The barber is deactivated mid-checkout → customer sees the barber-unavailable wording and a
      route back to rebook with someone else.
- [ ] A5. Someone else takes the same slot first → the second customer gets the "just booked by
      someone else" message and can pick another time.
- [ ] A6. The barber's hours are narrowed so the slot falls outside them mid-checkout.
- [ ] A7. Customer pays, then hits browser Back to the checkout page → must not be able to pay twice.
- [ ] A8. Customer has the same checkout open in two tabs and pays in one → the other tab must not
      produce a second charge.

## B. The Needs-Review worklist

Every one of these ends with a row an admin has to act on. The C# suite proves the flag gets set;
these prove **the admin can find it and clear it**.

Flag sites, from `FlagForReview` call sites — `BookingCanceller`, `BookingConflictCanceller` (x2),
`BarbersController` (barber deactivated), `DatesController` (closure), `SchedulesController` (hours
narrowed), `WebHookController` (x2 — orphaned charge, closure-during-payment), `EmailService` (x4 —
each cancellation email that exhausted its retries).

- [ ] B1. Barber deactivated with live bookings → each flagged row appears in the admin worklist with
      a reason the admin can actually read and act on.
- [ ] B2. Closure created over live bookings → same.
- [ ] B3. Hours narrowed so bookings fall outside → same, with the outside-hours wording.
- [ ] B4. The worklist count badge matches the number of flagged rows.
- [ ] B5. Mark-reviewed clears the row, and the count drops.
- [ ] B6. **Two problems on one booking** — flag it for hours, then for a closure. Both notes must be
      readable; the second must not erase the first. (This is the specific bug `BookingReview` was
      written to prevent, so it's worth a browser test.)
- [ ] B7. Editing a flagged booking does NOT clear the flag, and the row is still marked when the
      admin lands back on the table.
- [ ] B8. A booking flagged for a *failed refund* tells the admin the money is still with the shop —
      that's the note that costs real money if it's unreadable.

## C. Excluding and re-including a barber's bookings

The two directions of the same lever: work is taken away from a barber, then given back.

- [ ] C1. Narrowing hours over existing bookings raises the conflict warning **before** saving, and
      names the affected bookings.
- [ ] C2. Cancelling at that warning changes nothing — the schedule and the bookings are untouched.
- [ ] C3. Confirming saves the schedule and flags the affected bookings.
- [ ] C4. Widening the hours back tells the admin which review notes it has just made stale.
- [ ] C5. Deleting a schedule version — `SchedulesController.DeleteVersion` has **no orphan check**
      (documented gap, there's a characterisation test in C#). Prove what an admin actually sees, and
      write down whether it's acceptable.
- [ ] C6. "Accepting bookings" toggled OFF → the barber disappears from the customer picker but keeps
      their existing bookings and is still visible to staff.
- [ ] C7. Toggled back ON → the barber returns to the customer picker.
- [ ] C8. A barber winding down can still open and edit their own bookings (this regressed once —
      `includeUnbookable`).
- [ ] C9. Deactivating a barber signs them out (TokenVersion bumps) — prove the session actually dies.

## D. Money on screen

- [ ] D1. A €25.50 service shows as **€25.50** on the checkout total, the confirmation receipt, and
      the admin bookings table. (This was broken until recently — it read "€25.5".)
- [ ] D2. Booking with several services shows the correct summed total.
- [ ] D3. A cash booking shows the amount as owed, not as paid.
- [ ] D4. Admin edits the collected amount → the new figure shows in the table and the dashboard.

## E. Everything else worth a browser

- [ ] E1. The whole happy path: pick barber → date → time → details → pay with test card
      `4242 4242 4242 4242` → confirmation names the right barber, service, time and total.
- [ ] E2. Cash booking end to end.
- [ ] E3. Booking a slot outside the barber's hours is refused, with a readable reason.
- [ ] E4. A closed day is not selectable in the picker.
- [ ] E5. Admin reassigns a booking to another barber → the customer-facing record reflects the new
      barber. (The update email is C#-tested; this is the screen.)
- [ ] E6. Admin reschedules a booking → new time shows everywhere it should.
- [ ] E7. Password reset: request → the reset screen rejects a bad/expired token readably.
- [ ] E8. A customer hitting an unknown booking id gets the 404 page, not a blank screen.
- [ ] E9. Staff-only pages redirect a signed-out visitor to login rather than flashing the content.

---

## Not reachable from a browser — do not attempt

Add to this list rather than fighting a scenario that can't be driven. Each entry should say why.

- Stripe webhook signature failures — no browser involvement; covered in `WebhookRefundTests`.
- Email send failures and their retry-exhaustion flags — no UI; covered in `EmailFailureReviewTests`.
