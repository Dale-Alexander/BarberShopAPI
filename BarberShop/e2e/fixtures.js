/* Mirrors the constants in BarberShopAPI/Seed/SeedE2E.cs. Specs are allowed to hard-code these because
   the seed rebuilds them before every run - if you change one side, change the other.

   firstName is not a convenience: /api/Barbers/barbers-with-bookings projects BarberName from
   User.Name alone, so the customer booking page shows barbers by FIRST NAME only. Assert on the full
   name there and you'll be chasing a failure that isn't a bug. */
export const BARBER_ONE = { fullName: "Luke Camilleri", firstName: "Luke", email: "barber1@e2e.test" };
export const BARBER_TWO = { fullName: "Mark Bugeja", firstName: "Mark", email: "barber2@e2e.test" };

export const SERVICES = [
    { name: "Skin Fade", price: "25.50", durationMin: 30 },
    { name: "Beard Trim", price: "12.00", durationMin: 20 },
    { name: "Cut and Beard", price: "35.00", durationMin: 45 },
];

export { adminEmail, adminPassword } from "../playwright.config.js";
