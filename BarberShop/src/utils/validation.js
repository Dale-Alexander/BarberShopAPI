// Client-side validation mirroring the backend rules so bad input is caught before the
// round-trip. The server still re-validates every field. Each function returns a
// human-readable error string to show the user, or null when valid.

// --- Person names (customer / barber) ------------------------------------------------------
// The full name is split into first/last and stored in User.Name/User.Surname (nvarchar(50)
// each), so each part is capped at 50; the whole thing is capped at 100 to match [MaxLength(100)]
// on the view models. A person isn't "123", so digits are rejected. Mirrors IsValidName in
// BookingsController / BarbersController.

// The "obvious" problems, surfaced inline as the user types. Deliberately does NOT include the
// min-length rule: a half-typed name ("J") shouldn't flash an error while someone is still
// typing - that one is only enforced on submit (see validateName). Note an EMPTY string passes
// every rule here, so callers using this to enable a submit button will enable it on a blank name.
export const validateNameInline = (name) => {
    const trimmed = (name ?? "").trim();
    if (/\d/.test(trimmed)) return "Name cannot contain numbers";
    if (trimmed.length > 100) return "Name is too long";
    const parts = trimmed.split(/\s+/).filter(Boolean);
    if ((parts[0] ?? "").length > 50 || parts.slice(1).join(" ").length > 50) return "Name is too long";
    return null;
};

// Full check used on submit. Everything validateNameInline flags, plus the 2-character minimum -
// the one rule that only ever surfaces once the user has actually tried to submit.
export const validateName = (name) => {
    const inline = validateNameInline(name);
    if (inline) return inline;
    if ((name ?? "").trim().length < 2) return "Name must be at least 2 characters";
    return null;
};

// --- Email ---------------------------------------------------------------------------------
// Same shape the backend's [EmailAddress] accepts, plus the [MaxLength(100)] cap.
export const validateEmail = (email) => {
    const trimmed = (email ?? "").trim();
    if (!trimmed) return "Email is required";
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(trimmed)) return "Please enter a valid email address";
    if (trimmed.length > 100) return "Email is too long";
    return null;
};

// --- Password ------------------------------------------------------------------------------
// [MinLength(6)] everywhere; the max differs per view model (barber create = 100, reset = 40),
// so it's a parameter.
export const validatePassword = (password, { max = 100 } = {}) => {
    const value = password ?? "";
    if (value.length < 6) return "Password must be at least 6 characters";
    if (value.length > max) return `Password must be at most ${max} characters`;
    return null;
};
