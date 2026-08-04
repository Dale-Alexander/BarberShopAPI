using BarberShopAPI.Common;

namespace BarberShopAPI.Tests
{
    /* The single definition of "what is a usable full name, and how does it split" - now behind every path
     * that takes a person's name: barber create/update/revive, both booking-create paths, the booking
     * update, and the admin seed.
     *
     * No database, so this is a plain unit test rather than an IntegrationTestBase one.
     *
     * The 50-character cap is the reason these assertions exist. User.Name and User.Surname are
     * nvarchar(50); before this was one function, a name was validated by one split and then stored by a
     * separately hand-written one, so the two could disagree and an over-long half would surface as a
     * truncation error at SaveChanges instead of a clean 400. */
    public class PersonNameTests
    {
        [Theory]
        [InlineData("Joe Borg", "Joe", "Borg")]
        [InlineData("Joe", "Joe", "")]                                  // mononym: surname is blank, still valid
        [InlineData("  Joe   Borg  ", "Joe", "Borg")]                   // extra whitespace collapses
        [InlineData("Maria Grazia Vella", "Maria", "Grazia Vella")]     // everything after the first word
        [InlineData("O'Brien", "O'Brien", "")]                          // apostrophes are people too
        public void Valid_names_split_on_the_first_word(string input, string first, string last)
        {
            Assert.True(PersonName.TrySplit(input, out var f, out var l));
            Assert.Equal(first, f);
            Assert.Equal(last, l);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("J")]              // under two real characters
        [InlineData("Joe 123")]        // digits - a person isn't "123"
        [InlineData("J0e Borg")]       // digit buried mid-word
        public void Unusable_names_are_rejected_with_empty_halves(string? input)
        {
            Assert.False(PersonName.TrySplit(input, out var f, out var l));
            // Both halves must be blank on failure: the booking path relies on it for a nameless walk-in,
            // where TrySplit legitimately returns false and the empty strings are what get stored.
            Assert.Equal("", f);
            Assert.Equal("", l);
        }

        /* The cap is per HALF, not on the whole string - each is stored in its own nvarchar(50) column. */
        [Fact]
        public void Each_half_is_capped_at_the_column_width()
        {
            var atLimit = new string('a', PersonName.MaxPartLength);
            var overLimit = new string('a', PersonName.MaxPartLength + 1);

            Assert.True(PersonName.TrySplit($"{atLimit} {atLimit}", out _, out _));
            Assert.False(PersonName.TrySplit($"{overLimit} Borg", out _, out _));
            Assert.False(PersonName.TrySplit($"Joe {overLimit}", out _, out _));

            // A long surname is the sum of the words after the first, so it can bust the cap even when
            // no single word does.
            var thirtyFive = new string('b', 35);
            Assert.False(PersonName.TrySplit($"Joe {thirtyFive} {thirtyFive}", out _, out _));
        }
    }
}
