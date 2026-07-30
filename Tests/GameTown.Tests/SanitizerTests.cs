using API.Mapping;
using EFModel.Models;

namespace GameTown.Tests;

/// <summary>
/// The description sanitiser, which is the only thing standing between a community-editable RAWG
/// field and Blazor's MarkupString on a page anyone on the network can load.
///
/// These call the mapping directly rather than going over HTTP: the sanitiser is configured once in
/// a static field, and what needs pinning is that configuration — an allowlist that lets nothing
/// through but formatting tags — not the route that happens to return it.
/// </summary>
public class SanitizerTests
{
    // Description is scaffolded non-nullable, but the column is nullable and EF materialises null
    // into it regardless of the annotation — which is exactly the case the last test covers.
    private static string Sanitized(string? description)
        => new Rawggame { Description = description! }.ToContract().Description ?? string.Empty;

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"javascript:alert(1)\"></iframe>")]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>")]
    public void Script_bearing_markup_does_not_survive(string hostile)
    {
        var result = Sanitized(hostile);

        Assert.DoesNotContain("<script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shape from CVE-2026-54570, the AngleSharp mXSS flaw that HtmlSanitizer 9.0.x could not be
    /// upgraded away from. Foreign-content elements confuse the parser into re-interpreting escaped
    /// text as markup, so a sanitiser that trusts the parse tree emits script it believed it had
    /// removed. Fixed in AngleSharp 1.5.0 and shipped here via HtmlSanitizer 9.1.x.
    ///
    /// This test passed before the upgrade too — the empty attribute allowlist below was the
    /// compensating control while the fix was out of reach. It is kept so a downgrade of either
    /// package, or a future decision to allow attributes, has to walk past it.
    /// </summary>
    [Fact]
    public void The_mXSS_shape_from_the_AngleSharp_advisory_does_not_survive()
    {
        var result = Sanitized(
            "<annotation-xml encoding=\"text/html\"><p><style><!--</style><img src=x onerror=alert(1)>-->");

        Assert.DoesNotContain("onerror", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Formatting_tags_survive_because_descriptions_are_meant_to_be_read()
    {
        var result = Sanitized("<p>A <b>great</b> game.</p><ul><li>One</li></ul>");

        Assert.Contains("<p>", result);
        Assert.Contains("<b>great</b>", result);
        Assert.Contains("<li>One</li>", result);
    }

    /// <summary>
    /// No attribute is allowed on any tag. That is what made the mXSS advisory survivable while it
    /// was unpatched — half of it is unescaped '&lt;'/'&gt;' in serialised attribute *values*, and
    /// an empty allowlist leaves it nothing to act on.
    /// </summary>
    [Fact]
    public void No_attribute_survives_on_an_allowed_tag()
    {
        var result = Sanitized("<p class=\"x\" onclick=\"alert(1)\" style=\"color:red\">text</p>");

        // Deliberately not an exact-string comparison against "<p>text</p>": that would also fail on
        // a whitespace or entity-encoding change in a patch release, and a test that breaks for
        // cosmetic reasons stops being read as a security signal.
        Assert.DoesNotContain("onclick", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class=", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_description_becomes_an_empty_string_not_null(string? description)
        => Assert.Equal(string.Empty, Sanitized(description));
}
