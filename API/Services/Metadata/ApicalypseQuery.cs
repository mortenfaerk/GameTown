using System.Text;

namespace API.Services.Metadata;

/// <summary>
/// Builds IGDB query bodies, and escapes anything a user typed before it goes into one.
///
/// <b>This class exists because IGDB queries are a string body, not parameters.</b> RAWG took its
/// search term as a query-string value that RestSharp encoded, so a contributor could type whatever
/// they liked and the worst case was no results. Apicalypse has no such layer: the term is
/// interpolated into a statement, between quotes, in a body that also carries <c>fields</c>,
/// <c>where</c> and <c>limit</c> clauses:
///
/// <code>fields name, slug; search "QUERY"; where game_type = 0; limit 20;</code>
///
/// An unescaped double quote in QUERY therefore closes the string and everything after it is parsed
/// as query language — a second statement, a replaced <c>where</c>, a different <c>fields</c> list.
/// Verified against the live API while writing this: IGDB does not reject the broken-out query, it
/// answers it. There is no error to notice.
///
/// The blast radius is smaller than SQL injection (IGDB is a read-only public catalogue, reached with
/// our credentials, and nothing it returns is trusted downstream — images still go through
/// ImageFetcher, descriptions still through the sanitiser) but it is not nothing: the search endpoint
/// is a Contributor-authenticated proxy that would be executing caller-authored queries against our
/// rate limit, and any future clause we add would be equally rewritable.
///
/// So: everything user-typed goes through <see cref="EscapeString"/>, and nothing is interpolated
/// raw. Numbers are formatted invariantly and bounded by the caller.
/// </summary>
public static class ApicalypseQuery
{
    /// <summary>
    /// Escapes a value for use inside an Apicalypse double-quoted string.
    ///
    /// Backslash first — reversing the order would double-escape the backslashes this method itself
    /// introduces, turning <c>"</c> into <c>\\"</c>, which closes the string again. That ordering bug
    /// is the classic way an escaper looks right and does nothing.
    ///
    /// Control characters (including the newline that would otherwise end a statement) are dropped
    /// rather than escaped: no legitimate game title contains one, and dropping needs no support from
    /// the far end's parser.
    /// </summary>
    public static string EscapeString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var escaped = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (char.IsControl(c)) continue;

            if (c is '\\' or '"') escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>
    /// A quoted, escaped Apicalypse string literal — quotes included, so callers cannot add their own
    /// and accidentally place them outside the escaping.
    /// </summary>
    public static string Quote(string? value) => $"\"{EscapeString(value)}\"";
}
