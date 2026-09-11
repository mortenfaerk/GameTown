namespace API.Services.Lan;

/// <summary>
/// How much a ranked candidate list is worth showing a person.
///
/// This is a statement about PRESENTATION and nothing else. The automatic matcher's bar is still
/// normalised equality and only that — see <see cref="SuggestionMatcher"/>. Nothing here is ever
/// allowed to link a suggestion on its own.
/// </summary>
public enum MatchConfidence
{
    /// <summary>Nothing in the library is plausibly this game. The candidate list is noise.</summary>
    Weak,

    /// <summary>Something is close, but not close enough to trust without reading it.</summary>
    Ambiguous,

    /// <summary>One candidate stands clear of the rest. Safe to propose pre-ticked.</summary>
    Strong,
}

/// <summary>
/// Bands a ranked candidate list so the wishlist can show the three kinds of row differently.
///
/// WHY THIS EXISTS. <see cref="SuggestionMatcher.Rank"/> returns the top ten of everything scoring
/// above zero, and "above zero" is a very low bar — so the list is always ten items long whether or
/// not any of them is right. Measured across a 129-game library and 78 wishlist rows, 41% of the
/// queue had a top candidate below 0.2: ten wrong answers each, rendered identically to the rows
/// where the first entry was correct. Quantity was constant, quality was not, and the screen said
/// nothing about the difference.
///
/// SCORE ALONE IS NOT THE SIGNAL — SCORE AND MARGIN ARE. This is the part that is easy to get wrong,
/// and the reason a plain threshold was rejected. Measured against a 37-game shelf:
///
///   "helldivers"             -> Helldivers 2          0.633, margin 0.527   correct
///   "worms armageddon lan"   -> Worms Armageddon      0.720, margin 0.600   correct
///   "Deep Rock"              -> Deep Rock Galactic    0.600, margin 0.500   correct
///   "counter strike"         -> Counter-Strike 2      0.750, margin 0.269   a real choice, CS:GO behind it
///   "Age of Empires 2"       -> Age of Empires IV     0.825, margin 0.242   WRONG, AoE II: DE is behind it
///   "Battlefield 2142"       -> Battlefield 1942      0.650, margin 0.025   WRONG, 2142 not in the library
///   "the jackbox party pack" -> Jackbox Party Pack 8  0.810, margin 0.000   coin flip, Pack 9 ties exactly
///
/// The highest-scoring row on that list is wrong. "Age of Empires 2" normalises to
/// `age-of-empires-2`, which is one edit from `age-of-empires-4`, while the correct
/// `age-of-empires-2-definitive-edition` is penalised for its length — so a score threshold alone
/// would pre-tick the wrong game at 0.825. What separates the correct rows from the wrong ones is not
/// the score, it is the distance to the runner-up.
///
/// The thresholds live here rather than in the page so they can be tested without a browser, and so
/// there is one place to change them. See <c>LanMatchingTests</c>, where the six rows above are
/// pinned — the negatives especially, because a band that is generous looks better on a page of
/// examples and then links the wrong game in somebody's library.
/// </summary>
public static class MatchConfidenceBands
{
    /// <summary>
    /// A top candidate below this is not worth rendering however the rest of the field looks.
    ///
    /// At 0.2 a "match" is typically one shared short token: "q3a" reaching "Squad", "CS2" reaching
    /// "Rust", "asdf" reaching ten games at a tenth apiece.
    /// </summary>
    public const double WeakBelow = 0.2;

    /// <summary>The score a leader needs before its lead over the runner-up is even considered.</summary>
    public const double StrongAtLeast = 0.6;

    /// <summary>
    /// How far clear of the runner-up the leader must be to be proposed pre-ticked.
    ///
    /// 0.35 is not a guess, and an earlier draft of 0.15 was wrong in the one way that matters. The
    /// measured margins separate almost perfectly into two groups with nothing in between:
    ///
    ///     correct     0.500  0.527  0.600  0.886
    ///     not correct 0.000  0.025  0.242  0.269
    ///
    /// A 0.15 bar puts "Age of Empires 2" -> Age of Empires IV (0.242) in the STRONG band, pre-ticked,
    /// one bulk click from being pushed to Discord — with the correct "Age of Empires II: Definitive
    /// Edition" sitting right behind it at 0.583. The bar has to sit above 0.269 and below 0.500;
    /// 0.35 is the middle of that gap.
    /// </summary>
    public const double StrongMargin = 0.35;

    /// <summary>
    /// A leader this weak has to be clearly ahead to be worth showing at all — see
    /// <see cref="FlatFieldMargin"/>.
    /// </summary>
    public const double FlatFieldBelow = 0.5;

    /// <summary>
    /// The lead a mediocre leader needs before its list counts as an opinion rather than noise.
    ///
    /// This is the same insight as <see cref="StrongMargin"/>, applied at the other end. "swat 4"
    /// scores 0.414 against Trine 4 with Battlefield 4 at 0.392 behind it, and "mario kart" scores
    /// 0.200 against Magicka with Magicka 2 tied exactly — a flat field of equally-bad matches, which
    /// is what the matcher having no opinion looks like. SWAT 4 and Mario Kart are not in the library
    /// at all, so every one of those candidates is wrong, and the score alone does not say so.
    ///
    /// It is deliberately gated on a low <see cref="FlatFieldBelow"/>, because a tie between two GOOD
    /// candidates is the opposite case: "the jackbox party pack" ties Pack 8 and Pack 9 at 0.810, and
    /// that is a real choice a person has to make rather than noise to hide.
    /// </summary>
    public const double FlatFieldMargin = 0.1;

    /// <summary>
    /// Bands a candidate list, best-first, as <see cref="SuggestionMatcher.Rank"/> returns it.
    ///
    /// Read it as one idea stated at both ends: the margin is the signal. A leader well clear of the
    /// field can be proposed; a mediocre leader in a flat field is the matcher shrugging, and the
    /// screen should say "nothing in the library is this game" rather than offer ten ways to be wrong.
    ///
    /// A single candidate has nothing to compete with, so its margin is its own score — one plausible
    /// answer and no runner-up is the clearest case there is, not an unmeasurable one.
    /// </summary>
    public static MatchConfidence Band(IReadOnlyList<double> scoresDescending)
    {
        if (scoresDescending.Count == 0) return MatchConfidence.Weak;

        var top = scoresDescending[0];
        if (top < WeakBelow) return MatchConfidence.Weak;

        var margin = top - (scoresDescending.Count > 1 ? scoresDescending[1] : 0d);

        if (top < FlatFieldBelow && margin < FlatFieldMargin) return MatchConfidence.Weak;

        return top >= StrongAtLeast && margin >= StrongMargin
            ? MatchConfidence.Strong
            : MatchConfidence.Ambiguous;
    }

    /// <summary>The wire form: lower-case, so the contract carries a code rather than a C# enum name.</summary>
    public static string Name(MatchConfidence confidence) => confidence switch
    {
        MatchConfidence.Strong => "strong",
        MatchConfidence.Ambiguous => "ambiguous",
        _ => "weak",
    };

    /// <summary>
    /// The most candidates worth putting in front of a person for one suggestion.
    ///
    /// <see cref="SuggestionMatcher.Rank"/> takes ten because it is a general-purpose ranking. Ten is
    /// far too many to read per row when there are dozens of rows.
    /// </summary>
    public const int MostCandidates = 5;

    /// <summary>
    /// How close to the leader a candidate has to score to be shown beside it, as a fraction.
    ///
    /// Banding removed the rows where EVERY candidate is noise. This removes the noise TAIL on the
    /// rows that are worth showing, which is the same problem one level down: "Battlefield 2142"
    /// leads with four Battlefield titles at 0.60–0.65 and then lists Half-Life 2, Borderlands 2,
    /// Castle Crashers, Dirt Rally 2.0 and FIFA 23 at 0.10–0.13, every one of them there because it
    /// ends in a digit. The four are a real choice; the rest are six more ways to mis-click.
    ///
    /// A fraction of the leader rather than a fixed score, because "close" only means anything
    /// relative to what is being compared: 0.35 next to a 0.48 leader is a genuine alternative, and
    /// next to a 0.90 leader it is not.
    /// </summary>
    public const double NearestFraction = 0.5;

    /// <summary>
    /// Trims a ranked list to the candidates worth reading: those within
    /// <see cref="NearestFraction"/> of the leader, at most <see cref="MostCandidates"/> of them.
    ///
    /// Always keeps the leader, whatever it scores — a weak row is refused by the BAND, and a row
    /// that got this far should never render an empty list where a person expects options.
    /// </summary>
    public static List<T> Nearest<T>(IReadOnlyList<T> candidatesDescending, Func<T, double> scoreOf)
    {
        if (candidatesDescending.Count == 0) return [];

        var cutoff = scoreOf(candidatesDescending[0]) * NearestFraction;

        return [.. candidatesDescending
            .Where((candidate, index) => index == 0 || scoreOf(candidate) >= cutoff)
            .Take(MostCandidates)];
    }
}
