using System.Text;

namespace API.Services.Lan;

/// <summary>
/// Decides whether a suggestion typed in Discord and a title typed on an upload form are the same
/// game.
///
/// Pure and static on purpose — no DI, no database, no HTTP — because the interesting behaviour here
/// is entirely in the string handling, and testing it should not require booting an application. See
/// <c>LanMatchingTests</c>; it is written the way <c>ApicalypseTests</c> is.
///
/// The bar for an automatic match is deliberately high: normalised equality, nothing else. Fuzzy
/// scores exist in <see cref="Rank"/> and are only ever shown to a person. The failure being guarded
/// is not a missed match — someone links that by hand in a few seconds — it is a WRONG match, which
/// nobody checks, because an auto-linked game that looks plausible never gets a second look.
/// </summary>
public static class SuggestionMatcher
{
    /// <summary>
    /// A title's identity for matching. Empty when the input has nothing comparable in it.
    ///
    /// Lowercasing happens HERE, in C#, and must not be delegated to the database: GameTownGame.Title
    /// is COLLATE NOCASE, but SQLite's NOCASE folds ASCII only, so "Fælles Skærm" and "fælles skærm"
    /// compare as different under it. This is the one place a string typed in Discord has to meet a
    /// string typed on an upload form, by different people months apart, so the fold has to be the
    /// Unicode one. TagService.Slugify carries the same warning for the same reason.
    ///
    /// What it deliberately does NOT do: strip trailing numbers, subtitles, or parentheticals. Those
    /// are what distinguish "Portal" from "Portal 2" and "Warcraft 3" from "Warcraft 3: Warlock
    /// (Custom Map)", and a normaliser that removes them looks better on a page of examples while
    /// linking the wrong game in the library.
    /// </summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var tokens = Tokenize(title);
        if (tokens.Count == 0) return string.Empty;

        // Only as a LEADING article. "The Witcher 3" and "Witcher 3" are the same game; a "the"
        // anywhere else ("Tom Clancy's The Division") is part of the name and stays.
        if (tokens.Count > 1 && tokens[0] == "the") tokens.RemoveAt(0);

        return string.Join('-', tokens);
    }

    /// <summary>
    /// Splits a title into normalised tokens: lowercased, letters and digits kept by Unicode category
    /// so non-ASCII titles survive, everything else treated as a separator.
    ///
    /// Two substitutions happen during the split, and both are about one word being written two ways
    /// rather than about tidying: "&amp;" and "+" become "and", and a token that is a standalone roman
    /// numeral becomes its digits. The second is the most common divergence between a catalogue title
    /// and what someone types at a LAN — "Quake III Arena" against "Quake 3 Arena". It applies per
    /// token, never inside a word, so "Age of Empires II" converts and "Civ" keeps its letters.
    /// </summary>
    private static List<string> Tokenize(string title)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder(title.Length);

        void Flush()
        {
            if (builder.Length == 0) return;
            tokens.Add(RomanToDigits(builder.ToString()));
            builder.Clear();
        }

        foreach (var character in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else
            {
                Flush();

                // Written as a word by one person and as a symbol by the next, so it cannot be
                // dropped as punctuation: "Rock & Roll" and "Rock and Roll" must reach the same key.
                if (character is '&' or '+') tokens.Add("and");
            }
        }

        Flush();
        return tokens;
    }

    /// <summary>
    /// Converts a token that is entirely a roman numeral from 1 to 20 into digits, and leaves
    /// everything else alone.
    ///
    /// A lookup rather than a parser, because a parser accepts far too much: "i", "x" and "mix" all
    /// look like roman numerals to an algorithm, and folding a word like "mix" to a number would
    /// silently corrupt a title. The range stops at 20 for the same reason — past it the notation
    /// stops appearing in game titles and only the false positives are left.
    /// </summary>
    private static string RomanToDigits(string token) => token switch
    {
        "i" => "1", "ii" => "2", "iii" => "3", "iv" => "4", "v" => "5",
        "vi" => "6", "vii" => "7", "viii" => "8", "ix" => "9", "x" => "10",
        "xi" => "11", "xii" => "12", "xiii" => "13", "xiv" => "14", "xv" => "15",
        "xvi" => "16", "xvii" => "17", "xviii" => "18", "xix" => "19", "xx" => "20",
        _ => token,
    };

    /// <summary>
    /// Whether two titles are the same game as far as automatic matching is concerned.
    ///
    /// Empty never matches empty. A suggestion of "..." normalises to nothing and so does a library
    /// title of "???"; treating those as equal would link two unrelated rows on the strength of both
    /// being unreadable.
    /// </summary>
    public static bool IsExactMatch(string? left, string? right)
    {
        var normalised = NormalizeTitle(left);
        return normalised.Length > 0 && normalised == NormalizeTitle(right);
    }

    /// <summary>
    /// Orders library entries by how well they match a suggestion's name, best first.
    ///
    /// FOR HUMANS ONLY. The score is a presentation order, not a decision — nothing in the automatic
    /// path reads it, and no threshold in it means "close enough". Exact matches sort first and are
    /// flagged; everything after is a prompt for someone who knows the library.
    /// </summary>
    public static List<RankedMatch<T>> Rank<T>(
        string suggestionName, IEnumerable<T> library, Func<T, string> titleOf, int take = 10)
    {
        var target = NormalizeTitle(suggestionName);
        if (target.Length == 0) return [];

        var targetTokens = target.Split('-');

        return [.. library
            .Select(item =>
            {
                var candidate = NormalizeTitle(titleOf(item));
                var exact = candidate.Length > 0 && candidate == target;

                return new RankedMatch<T>(
                    item,
                    exact ? 1d : Similarity(target, targetTokens, candidate),
                    exact);
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.IsExact)
            .ThenByDescending(match => match.Score)
            .ThenBy(match => titleOf(match.Item))
            .Take(take)];
    }

    /// <summary>
    /// 0..1, blending how many words the two titles share with how close they are character by
    /// character.
    ///
    /// Both halves earn their place. Token overlap alone scores "Counter-Strike: Source" and
    /// "Counter-Strike 2" identically against "counter-strike"; edit distance alone punishes a
    /// correct match that carries a long subtitle. Neither is load-bearing — this only decides the
    /// order a person reads the options in.
    /// </summary>
    private static double Similarity(string target, string[] targetTokens, string candidate)
    {
        if (candidate.Length == 0) return 0;

        var candidateTokens = candidate.Split('-');
        var shared = targetTokens.Intersect(candidateTokens).Count();
        var overlap = (double)shared / Math.Max(targetTokens.Length, candidateTokens.Length);

        var distance = Levenshtein(target, candidate);
        var closeness = 1d - ((double)distance / Math.Max(target.Length, candidate.Length));

        return (overlap * 0.6) + (Math.Max(closeness, 0) * 0.4);
    }

    /// <summary>
    /// Edit distance, two rows rather than a full matrix — titles are short and this runs once per
    /// library entry per candidate lookup.
    /// </summary>
    private static int Levenshtein(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++) previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}

/// <summary>One ranked candidate. <paramref name="Score"/> orders the list and means nothing else.</summary>
public readonly record struct RankedMatch<T>(T Item, double Score, bool IsExact);
