# Scenario queue

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
      a closure is the webhook race, which needs Stripe to reach localhost; covered in
      `WebhookRefundTests`. What's tested here is the success screen's own behaviour.
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
- [ ] A6. The barber's hours are narrowed so the slot falls outside them mid-checkout.
- [ ] A7. Customer pays, then hits browser Back to the checkout page → must not be able to pay twice.
- [ ] A8. Customer has the same checkout open in two tabs and pays in one → the other tab must not
      produce a second charge.

> **Read before writing any more of section A or B.** A closure and a schedule change do NOT cancel
> a booking that is already confirmed — they FLAG it and leave it standing, money and all, for an
> admin to reassign, move or cancel by hand. Only PENDING bookings are cancelled outright. Any
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
- **A1, "admin cancels a booking mid-checkout" — not a real flow.** `admin-fetch` filters to
  `Payment != null` and defaults to COMPLETED, so a PENDING booking never appears in the admin list.
  An admin cannot see a checkout in progress, so cannot cancel one. What actually kills a live
  checkout is a closure (A2), a deactivated barber (A4), losing the slot (A5), or the expiry job.
