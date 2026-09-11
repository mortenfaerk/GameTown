using API.Services.Lan;

namespace GameTown.Tests;

/// <summary>
/// The rules that decide whether a suggestion typed in Discord is a game in the library.
///
/// No app and no HTTP — <see cref="SuggestionMatcher"/> is deliberately pure, so this is written the
/// way <c>ApicalypseTests</c> is. That matters here because the interesting half of the behaviour is
/// what the matcher REFUSES to fold together, and those cases are tedious to reach through an
/// endpoint and trivial to enumerate directly.
/// </summary>
public class LanMatchingTests
{
    [Theory]
    // Case alone. The whole reason the fold happens in C# rather than being left to the column's
    // NOCASE collation — see the next test for the half that NOCASE gets wrong.
    [InlineData("Counter-strike 2", "Counter-Strike 2")]
    [InlineData("COUNTER-STRIKE 2", "counter-strike 2")]
    // Punctuation and spacing, which nobody types consistently.
    [InlineData("Counter Strike 2", "Counter-Strike 2")]
    [InlineData("  Quake  Arena  ", "Quake Arena")]
    // Roman numerals: the most common divergence between a catalogue title and what people type.
    [InlineData("Quake III Arena", "Quake 3 Arena")]
    [InlineData("Age of Empires II", "Age of Empires 2")]
    [InlineData("Civilization VI", "Civilization 6")]
    // A leading article, and only a leading one.
    [InlineData("The Witcher 3", "Witcher 3")]
    // "and" written as a word by one person and a symbol by the next.
    [InlineData("Rock & Roll Racing", "Rock and Roll Racing")]
    [InlineData("Command & Conquer", "Command and Conquer")]
    public void Titles_that_are_the_same_game_match(string suggestion, string libraryTitle)
        => Assert.True(SuggestionMatcher.IsExactMatch(suggestion, libraryTitle),
            $"'{suggestion}' and '{libraryTitle}' should match "
            + $"('{SuggestionMatcher.NormalizeTitle(suggestion)}' vs '{SuggestionMatcher.NormalizeTitle(libraryTitle)}')");

    /// <summary>
    /// The reason lowercasing is not delegated to SQLite.
    ///
    /// GameTownGame.Title is COLLATE NOCASE, which folds ASCII only — so "Æ" and "æ" compare as
    /// different under it while "A" and "a" do not. A Danish library with a Danish LAN is exactly the
    /// deployment this feature is for, and the failure would be a title that silently never matches.
    /// </summary>
    [Theory]
    [InlineData("Fælles Skærm", "fælles skærm")]
    [InlineData("BLÅ Himmel", "blå himmel")]
    [InlineData("ÜBERSOLDIER", "übersoldier")]
    public void Non_ascii_titles_fold_by_case_too(string suggestion, string libraryTitle)
        => Assert.True(SuggestionMatcher.IsExactMatch(suggestion, libraryTitle));

    /// <summary>
    /// The half that matters more.
    ///
    /// A normaliser that strips trailing numbers or parentheticals passes every positive above and
    /// then links the wrong game — and unlike a missed match, which someone fixes by hand in seconds,
    /// a wrong auto-link is never looked at again. "Left 4 Dead" and "Left 4 Dead 2" are the case
    /// that makes it concrete: both are in plenty of libraries, and either is a plausible answer.
    /// </summary>
    [Theory]
    [InlineData("Portal", "Portal 2")]
    [InlineData("Left 4 Dead", "Left 4 Dead 2")]
    [InlineData("Counter-Strike", "Counter-Strike 2")]
    [InlineData("Warcraft 3", "Warcraft 3: Warlock (Custom Map)")]
    [InlineData("Quake", "Quake Arena")]
    [InlineData("Age of Empires II", "Age of Empires III")]
    // Not an article in this position, so it is part of the name and stays.
    [InlineData("Tom Clancy's The Division", "Tom Clancy's Division")]
    public void Titles_that_are_different_games_do_not_match(string left, string right)
        => Assert.False(SuggestionMatcher.IsExactMatch(left, right),
            $"'{left}' and '{right}' must NOT match "
            + $"(both normalised to '{SuggestionMatcher.NormalizeTitle(left)}')");

    /// <summary>
    /// A word that happens to be spellable in roman numerals must not become a number.
    ///
    /// This is why the conversion is a lookup of whole tokens rather than a parser: "mix", "did" and
    /// "civic" are all valid roman numerals to an algorithm, and folding one to digits would corrupt
    /// a title in a way that reads as a matching bug years later.
    /// </summary>
    [Theory]
    [InlineData("Mix")]
    [InlineData("Civic")]
    [InlineData("Did")]
    [InlineData("Dim")]
    public void Words_that_look_like_roman_numerals_are_left_alone(string word)
        => Assert.Equal(word.ToLowerInvariant(), SuggestionMatcher.NormalizeTitle(word));

    /// <summary>
    /// Two titles with nothing comparable in them are not "the same".
    ///
    /// Without this, a suggestion of "???" and a library entry called "..." both normalise to the
    /// empty string and auto-link to each other — two unrelated rows joined on the strength of both
    /// being unreadable, with no human ever asked.
    /// </summary>
    [Theory]
    [InlineData("???", "...")]
    [InlineData("", "")]
    [InlineData("   ", "!!!")]
    public void Titles_with_nothing_in_them_never_match(string left, string right)
        => Assert.False(SuggestionMatcher.IsExactMatch(left, right));

    [Fact]
    public void Ranking_puts_the_exact_match_first_and_flags_it()
    {
        string[] library = ["Portal 2", "Counter-Strike 2", "Counter-Strike: Source", "Quake Arena"];

        var ranked = SuggestionMatcher.Rank("counter-strike 2", library, title => title);

        Assert.Equal("Counter-Strike 2", ranked[0].Item);
        Assert.True(ranked[0].IsExact);

        // Everything else is a suggestion to a human and must not claim to be exact, however close it
        // scores — the automatic path reads IsExact and nothing else.
        Assert.All(ranked.Skip(1), match => Assert.False(match.IsExact));
    }

    [Fact]
    public void Ranking_offers_near_misses_so_a_person_can_choose()
    {
        string[] library = ["Counter-Strike: Source", "Portal 2", "Quake Arena"];

        // A typo, which is what a near miss actually looks like in a Discord channel. Note that
        // "counter strike source" would NOT be one — punctuation is already normalised away, so that
        // spelling is an exact match and would be linked automatically.
        var ranked = SuggestionMatcher.Rank("Counterstrike Source", library, title => title);

        // No exact match exists, so nothing may be flagged — but the plausible candidate must still
        // be offered, and first. A manual screen that returns nothing is a screen nobody can use.
        Assert.NotEmpty(ranked);
        Assert.Equal("Counter-Strike: Source", ranked[0].Item);
        Assert.All(ranked, match => Assert.False(match.IsExact));
    }

    [Fact]
    public void Ranking_a_name_with_nothing_in_it_returns_nothing()
        => Assert.Empty(SuggestionMatcher.Rank("???", new[] { "Portal 2" }, title => title));
}
