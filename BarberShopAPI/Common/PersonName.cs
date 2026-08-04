namespace BarberShopAPI.Common
{
    /* "Is this a usable full name, and what are its two halves?" - asked by every path that accepts a
     * person's name from a form: barbers (create, update, revive), customers (the two booking-create paths
     * and the booking update), and the admin seed.
     *
     * It used to be asked twice at each of those sites, in two different shapes. Each controller carried
     * its own private IsValidName - two copies, byte-for-byte identical - which split the name internally
     * just to check the length cap, threw the halves away, and returned a bool. The caller then re-split
     * the same string by hand to actually use the parts. Seven hand-rolled copies of
     * `parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : ""` across the codebase.
     *
     * The 50-character cap is the part that matters. User.Name and User.Surname are nvarchar(50), so a name
     * whose halves exceed that is a truncation error at SaveChanges rather than a clean 400 - and the split
     * has to be identical to the one the caller then stores, or validation passes on one shape while a
     * different shape gets written. One function returning both the verdict and the halves makes that
     * impossible to get wrong. */
    public static class PersonName
    {
        public const int MaxPartLength = 50;

        /// <summary>
        /// Splits a full name into first/surname on whitespace, first word to <paramref name="first"/> and
        /// everything after it to <paramref name="last"/>. Returns false - with both set to "" - when the
        /// name is unusable: empty, under two characters, containing digits (a person isn't "123"), or
        /// either half longer than the nvarchar(50) columns can hold.
        /// </summary>
        public static bool TrySplit(string? fullName, out string first, out string last)
        {
            first = last = "";
            if (string.IsNullOrWhiteSpace(fullName)) return false;

            var trimmed = fullName.Trim();
            if (trimmed.Length < 2) return false;              // at least 2 real characters
            if (fullName.Any(char.IsDigit)) return false;      // no digits

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var firstPart = parts.Length > 0 ? parts[0] : "";
            var lastPart = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
            if (firstPart.Length > MaxPartLength || lastPart.Length > MaxPartLength) return false;

            first = firstPart;
            last = lastPart;
            return true;
        }
    }
}
