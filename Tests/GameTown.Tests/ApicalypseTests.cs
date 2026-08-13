using API.Services.Metadata;

namespace GameTown.Tests;

/// <summary>
/// Escaping for IGDB query bodies — a vulnerability class this codebase did not have before the move
/// off RAWG, and the reason this file exists rather than the tests being folded in somewhere.
///
/// RAWG took its search term as a query-string parameter, which RestSharp encoded; the worst a
/// contributor could do by typing punctuation was get no results. Apicalypse has no such layer. The
/// term is interpolated into a statement, between quotes, in a body that also carries the fields,
/// where and limit clauses:
///
///     fields name, slug; search "QUERY"; where game_type = 0; limit 20;
///
/// so an unescaped double quote closes the string and everything after it is parsed as query
/// language. Verified against the live API while this was written: IGDB does not reject a broken-out
/// query, it answers it. There is no error to notice, which is why this is pinned rather than left to
/// code review.
/// </summary>
public class ApicalypseTests
{
    [Fact]
    public void An_ordinary_title_passes_through_unchanged()
    {
        Assert.Equal("\"Half-Life 2\"", ApicalypseQuery.Quote("Half-Life 2"));
    }

    /// <summary>
    /// The attack this exists to stop. Without escaping the rendered body would be
    /// <c>search "x"; where id = 1; //";</c> — a caller-authored where clause.
    /// </summary>
    [Fact]
    public void A_quote_cannot_close_the_search_string()
    {
        var quoted = ApicalypseQuery.Quote("x\"; where id = 1; //");

        // The injected quote survives as data, escaped, and the literal is still one string: exactly
        // two unescaped quotes, at the ends.
        Assert.Equal("\"x\\\"; where id = 1; //\"", quoted);
        Assert.Equal(2, CountUnescapedQuotes(quoted));
    }

    /// <summary>
    /// The ordering bug that makes an escaper look right and do nothing: escaping quotes first and
    /// backslashes second turns <c>"</c> into <c>\\"</c>, whose backslash is itself escaped — leaving
    /// the quote live and the string closed.
    /// </summary>
    [Fact]
    public void A_backslash_before_a_quote_does_not_re_open_the_string()
    {
        var quoted = ApicalypseQuery.Quote("ends with a backslash \\\" then more");

        Assert.Equal(2, CountUnescapedQuotes(quoted));
    }

    [Fact]
    public void A_trailing_backslash_cannot_escape_the_closing_quote()
    {
        var quoted = ApicalypseQuery.Quote("trailing\\");

        Assert.Equal("\"trailing\\\\\"", quoted);
        Assert.Equal(2, CountUnescapedQuotes(quoted));
    }

    /// <summary>
    /// Newlines end a statement in Apicalypse, so a term carrying one could append a whole clause
    /// without ever needing a quote. Dropped rather than escaped: no game title contains a control
    /// character, and dropping needs no cooperation from the far end's parser.
    /// </summary>
    [Theory]
    [InlineData("line\nbreak", "\"linebreak\"")]
    [InlineData("carriage\rreturn", "\"carriagereturn\"")]
    [InlineData("null\0byte", "\"nullbyte\"")]
    [InlineData("tab\there", "\"tabhere\"")]
    public void Control_characters_are_dropped(string input, string expected)
    {
        Assert.Equal(expected, ApicalypseQuery.Quote(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_renders_as_an_empty_literal(string? input)
    {
        Assert.Equal("\"\"", ApicalypseQuery.Quote(input));
    }

    /// <summary>
    /// Titles that legitimately contain punctuation must still be searchable — an escaper that
    /// mangles "Tom Clancy's" or "S.T.A.L.K.E.R." is safe and useless.
    /// </summary>
    [Theory]
    [InlineData("Tom Clancy's Rainbow Six")]
    [InlineData("S.T.A.L.K.E.R.: Shadow of Chernobyl")]
    [InlineData("Ōkami")]
    [InlineData("Sid Meier's Civilization VI")]
    [InlineData("Command & Conquer")]
    public void Legitimate_titles_survive_intact(string title)
    {
        Assert.Equal($"\"{title}\"", ApicalypseQuery.Quote(title));
    }

    /// <summary>
    /// Counts quotes that are NOT preceded by an odd number of backslashes — i.e. the ones that
    /// actually delimit the string as far as the parser is concerned. Two means the literal is
    /// well-formed; more means something inside it can be read as query language.
    /// </summary>
    private static int CountUnescapedQuotes(string rendered)
    {
        var count = 0;
        for (var i = 0; i < rendered.Length; i++)
        {
            if (rendered[i] != '"') continue;

            var backslashes = 0;
            for (var j = i - 1; j >= 0 && rendered[j] == '\\'; j--) backslashes++;

            if (backslashes % 2 == 0) count++;
        }
        return count;
    }
}
