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

    // ------------------------------------------------------------------ confidence banding

    /// <summary>
    /// The library the banding cases below are measured against.
    ///
    /// Held as a realistic shelf rather than trimmed to each case, because the whole point of banding
    /// is what a suggestion scores against EVERYTHING — "swat 4" is weak only because the games that
    /// out-rank the right answer are also present, and a three-item library would hide that.
    /// </summary>
    private static readonly string[] Library =
    [
        "Counter-Strike 2", "Counter-Strike: Global Offensive", "Team Fortress 2",
        "Left 4 Dead", "Left 4 Dead 2", "Portal", "Portal 2",
        "Age of Empires II: Definitive Edition", "Age of Empires IV",
        "Warcraft III: The Frozen Throne", "Quake III Arena", "Quake Live",
        "Battlefield 1942", "Battlefield 2", "Battlefield 4", "Battlefield V",
        "Trackmania United", "Trackmania Nations Forever", "Worms Armageddon",
        "Helldivers 2", "Deep Rock Galactic", "Lethal Company", "Trine 4",
        "Jackbox Party Pack 8", "Jackbox Party Pack 9", "Dota 2", "Payday 2",
        "Europa Universalis IV", "Hearts of Iron IV", "Squad", "Rust", "Darktide",
        "Magicka", "Magicka 2", "Overcooked! 2", "Borderlands 2", "Castle Crashers",
    ];

    private static MatchConfidence BandFor(string suggestion)
        => MatchConfidenceBands.Band(
            [.. SuggestionMatcher.Rank(suggestion, Library, title => title).Select(m => m.Score)]);

    /// <summary>
    /// The rows a person should be able to accept without reading: a clear leader, well ahead of the
    /// runner-up.
    ///
    /// Measured margins on these were 0.378–0.488, against 0.000–0.242 for everything in the next
    /// test. That gap is what the threshold sits in.
    /// </summary>
    [Theory]
    [InlineData("helldivers")]
    [InlineData("Deep Rock")]
    [InlineData("worms armageddon lan")]
    [InlineData("lethal company")]
    public void A_clear_leader_bands_strong(string suggestion)
        => Assert.Equal(MatchConfidence.Strong, BandFor(suggestion));

    /// <summary>
    /// THE CASES THAT KILL A PLAIN SCORE THRESHOLD, and the reason the margin exists at all.
    ///
    /// Every one of these scores well — two of them score HIGHER than any row in the test above — and
    /// every one is wrong or a coin flip:
    ///
    ///   "Age of Empires 2"       -> Age of Empires IV        0.825, margin 0.242. Wrong: the roman
    ///                               fold makes `age-of-empires-2` one edit from `age-of-empires-4`,
    ///                               while the correct `...-2-definitive-edition` is penalised for
    ///                               its length.
    ///   "the jackbox party pack" -> Jackbox Party Pack 8     0.810, margin 0.000. Pack 9 ties it
    ///                               exactly — a coin flip presented as an answer.
    ///   "Battlefield 2142"       -> Battlefield 1942         0.650, margin 0.025. Wrong, and 2142 is
    ///                               not in the library at all, so no candidate is right.
    ///
    /// If one of these ever bands Strong, the screen will arrive with it pre-ticked and a bulk link
    /// will push it to Discord without anybody reading it. That is the failure this whole mechanism
    /// exists to prevent, so these are the assertions that matter most in this file.
    /// </summary>
    [Theory]
    [InlineData("Age of Empires 2")]
    [InlineData("the jackbox party pack")]
    [InlineData("Battlefield 2142")]
    // Not wrong, but a genuine choice: CS:GO sits 0.269 behind, and which one somebody means at a LAN
    // is not something a string distance knows.
    [InlineData("counter strike")]
    public void A_high_score_with_a_thin_margin_bands_ambiguous(string suggestion)
        => Assert.Equal(MatchConfidence.Ambiguous, BandFor(suggestion));

    /// <summary>
    /// Rows where nothing in the library is the game, which measured at 41% of a real queue.
    ///
    /// These are the ones the screen must NOT offer candidates for. Each still produces ten ranked
    /// results, and rendering those buys nothing but a chance of a mis-click on an outbound push.
    ///
    /// The first two are here on the FLAT-FIELD rule rather than on a low score, and they are the
    /// reason that rule exists: "swat 4" leads at 0.414 with Battlefield 4 at 0.392 behind it, and
    /// "mario kart" leads at 0.200 with Magicka 2 tied exactly. Neither game is in the library, so
    /// every candidate is wrong — and a bare score bar low enough to catch them would also hide the
    /// real choices in the test above.
    /// </summary>
    [Theory]
    [InlineData("swat 4")]
    [InlineData("mario kart")]
    [InlineData("q3a")]
    [InlineData("asdf")]
    [InlineData("CS2")]
    public void Nothing_plausible_bands_weak(string suggestion)
        => Assert.Equal(MatchConfidence.Weak, BandFor(suggestion));

    /// <summary>
    /// A suggestion that normalises to nothing has no candidates at all, which is weak rather than an
    /// error. The screen offers "set aside", which is the right answer for "???".
    /// </summary>
    [Fact]
    public void No_candidates_at_all_bands_weak()
        => Assert.Equal(MatchConfidence.Weak, MatchConfidenceBands.Band([]));

    /// <summary>
    /// One candidate and no runner-up is the clearest case there is, not an unmeasurable one — so the
    /// margin is the score itself rather than zero.
    ///
    /// Written against the band function directly: reaching a one-candidate library through Rank
    /// would be testing Rank's filtering instead.
    /// </summary>
    [Fact]
    public void A_lone_candidate_competes_with_nothing()
    {
        Assert.Equal(MatchConfidence.Strong, MatchConfidenceBands.Band([0.7]));

        // Still has to clear the score bar. A lone bad candidate is a lone bad candidate — and note
        // that 0.45 is NOT caught by the flat-field rule, because with no runner-up its margin is its
        // own score and it is comfortably clear of an empty field.
        Assert.Equal(MatchConfidence.Ambiguous, MatchConfidenceBands.Band([0.45]));
        Assert.Equal(MatchConfidence.Weak, MatchConfidenceBands.Band([0.1]));
    }

    /// <summary>
    /// The boundaries, stated as the thresholds themselves rather than as literals — a change to a
    /// constant should move these tests with it, not break them.
    /// </summary>
    [Fact]
    public void The_bands_are_inclusive_at_their_thresholds()
    {
        var top = MatchConfidenceBands.StrongAtLeast;
        var margin = MatchConfidenceBands.StrongMargin;

        Assert.Equal(MatchConfidence.Strong, MatchConfidenceBands.Band([top, top - margin - 0.01]));

        // Under the margin is not strong, however good the leader looks.
        Assert.Equal(MatchConfidence.Ambiguous, MatchConfidenceBands.Band([top, top - margin + 0.01]));

        // Under the score bar is not strong either, however far clear it is.
        Assert.Equal(MatchConfidence.Ambiguous, MatchConfidenceBands.Band([top - 0.01, 0]));

        Assert.Equal(MatchConfidence.Ambiguous, MatchConfidenceBands.Band([MatchConfidenceBands.WeakBelow + 0.01]));
        Assert.Equal(MatchConfidence.Weak, MatchConfidenceBands.Band([MatchConfidenceBands.WeakBelow - 0.01]));
    }

    /// <summary>
    /// The flat-field rule, at its own boundaries.
    ///
    /// The pair below differ only in the runner-up, which is exactly the distinction the rule is for:
    /// the same leading score is noise in a crowd and an answer on its own.
    /// </summary>
    [Fact]
    public void A_mediocre_leader_needs_a_lead_to_be_shown_at_all()
    {
        // Stepped clear of the thresholds rather than sitting exactly on them: these are doubles, and
        // a test that pins an exact boundary pins the arithmetic's rounding rather than the rule.
        var top = MatchConfidenceBands.FlatFieldBelow - 0.01;
        var margin = MatchConfidenceBands.FlatFieldMargin;

        Assert.Equal(MatchConfidence.Weak, MatchConfidenceBands.Band([top, top - margin + 0.01]));
        Assert.Equal(MatchConfidence.Ambiguous, MatchConfidenceBands.Band([top, top - margin - 0.01]));

        // Above the flat-field ceiling the rule stops applying, so a dead tie between two good
        // candidates stays a choice rather than being hidden — "the jackbox party pack" is this shape.
        var good = MatchConfidenceBands.FlatFieldBelow;
        Assert.Equal(MatchConfidence.Ambiguous, MatchConfidenceBands.Band([good, good]));
    }

    /// <summary>
    /// Banding is presentation and must never widen the automatic path.
    ///
    /// Stated as a test because it is the invariant somebody will be tempted to break the first time
    /// a Strong row turns out to be right nine times in a row: auto-linking those would ship the
    /// "Age of Empires 2" mistake above, silently, on a timer.
    /// </summary>
    [Fact]
    public void A_strong_band_is_not_an_exact_match()
    {
        var ranked = SuggestionMatcher.Rank("helldivers", Library, title => title);

        Assert.Equal(MatchConfidence.Strong, MatchConfidenceBands.Band([.. ranked.Select(m => m.Score)]));
        Assert.All(ranked, match => Assert.False(match.IsExact));
        Assert.False(SuggestionMatcher.IsExactMatch("helldivers", "Helldivers 2"));
    }
}
