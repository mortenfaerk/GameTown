using API.Services.Archives;
using API.Services.Metadata;
using EFModel.Models;
using Ganss.Xss;
using GameTown.Contracts.Games;

namespace API.Mapping;

/// <summary>
/// Entity -> wire-contract mapping for games. Lives here rather than in Contracts so that
/// the Contracts project stays free of any EF Core dependency (it is referenced by the
/// Blazor WASM client).
/// </summary>
public static class GameMappings
{
    /// <summary>
    /// Descriptions are HTML, and the client renders them with MarkupString, which bypasses Blazor's
    /// encoding. They come from a community-editable catalogue, so the string is untrusted: without
    /// this an entry could carry script into the public game page.
    ///
    /// Still required after the move off RAWG, and arguably more so. The column now holds two things:
    /// HTML that RAWG served and migration 007 carried across unchanged — including anything stored
    /// before this sanitiser existed — and IGDB summaries, which arrive as plain text and are encoded
    /// into HTML at ingest. Sanitising on the way OUT rather than on ingest is what cleans the first
    /// category with no migration, and it is why the two can share a column safely.
    ///
    /// Configure once — Sanitize() is safe to call concurrently.
    /// </summary>
    private static readonly HtmlSanitizer DescriptionSanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();

        // Formatting tags only. No <a>: descriptions gain little from links here, and dropping
        // them lets the attribute allowlist be empty (see below), which matters for the known issue
        // in this version's parser.
        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[] { "p", "br", "b", "i", "em", "strong", "ul", "ol", "li" })
            sanitizer.AllowedTags.Add(tag);

        // Nothing is allowed through here — no attribute, no scheme, no CSS property.
        //
        // That started as compensation for an accepted risk: HtmlSanitizer 9.0.x hard-pinned
        // AngleSharp [0.17.1], which carries CVE-2026-54570, an mXSS flaw in the parser this
        // sanitiser trusts. Half of it is unescaped '<'/'>' in serialised *attribute values*, so an
        // empty attribute allowlist left it nothing to bite on. The pin is gone (9.1.x depends on
        // AngleSharp 1.6.0, where it is fixed) but the allowlist stays: a description needs no
        // attributes, and this is defence in depth against the next parser bug rather than a
        // workaround for the last one. SanitizerTests pins the behaviour.
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedCssProperties.Clear();

        return sanitizer;
    }

    private static string Sanitize(string? html)
        => string.IsNullOrEmpty(html) ? string.Empty : DescriptionSanitizer.Sanitize(html);

    /// <summary>
    /// The same sanitiser, for a description that never came from the database.
    ///
    /// The metadata preview endpoint maps a provider record straight to a contract without storing it,
    /// so it does not pass through <see cref="ToContract(MetadataGame)"/> — and would otherwise be the
    /// one path serving an unsanitised description to a MarkupString.
    /// </summary>
    public static string SanitizeDescription(string? html) => Sanitize(html);

    /// <summary>
    /// <paramref name="currentProviderId"/> is what decides <see cref="GameContract.CanRefreshMetadata"/>.
    ///
    /// It has to be passed in rather than read here: this class is static and deliberately free of DI,
    /// and the alternative — letting the browser compare the row's provider against a hardcoded
    /// "igdb" — would put the name of the current provider in two places that could disagree.
    /// </summary>
    public static GameContract ToContract(this GameTownGame game, string? currentProviderId = null) => new()
    {
        Id = game.Id,
        Title = game.Title,
        HowTo = game.HowTo,
        Size = game.Size,
        BoxArtUrl = game.BoxArtUrl,
        GuideBaked = game.GuideBaked,
        // Derived from the stored path but never exposing it: the client needs to know whether the
        // toggle is available, not where the archive lives.
        CanBakeGuide = ArchiveGuideService.IsSupported(game.Url),
        // Ordered here rather than relying on the join's natural order, which is the primary key's
        // and therefore effectively random to a reader. Quick-add tags first so the ones people scan
        // for — split screen, LAN, co-op — sit in a stable place on every card.
        Tags = [.. game.Tags
            .OrderByDescending(t => t.IsQuickAdd)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .Select(t => t.ToContract())],
        Metadata = game.Metadata?.ToContract(),
        // False for a game still carrying RAWG metadata: there is nothing to refresh from, because
        // the service is gone. Computed here rather than in the browser for the reason above.
        CanRefreshMetadata = game.Metadata is not null
                             && currentProviderId is not null
                             && game.Metadata.Provider == currentProviderId,
        // Distinct, because two people asking for the same game at the same LAN is one badge, not two.
        // Ordered descending so the most recent event leads — event names carry their number
        // ("HCP #37 (2026)"), which sorts usefully, and there is no date on a suggestion to sort by.
        SuggestedFor = [.. game.LanSuggestions
            .Select(s => s.LanEventName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)],
        PlayedAtLan = game.LanSuggestions.Any(s => s.Played)
    };

    public static TagContract ToContract(this Tag tag) => new()
    {
        Id = tag.Id,
        Name = tag.Name,
        Slug = tag.Slug,
        IsQuickAdd = tag.IsQuickAdd,
        // GameCount is deliberately left at zero. Populating it here would mean a count query per tag
        // per game per page; the tag list endpoint is where a caller that needs counts gets them.
    };

    public static GameMetadataContract ToContract(this MetadataGame m) => new()
    {
        Id = m.Id,
        Provider = m.Provider,
        ExternalId = m.ExternalId,
        Slug = m.Slug,
        Name = m.Name,
        Description = Sanitize(m.Description),
        Released = m.Released,
        CriticScore = m.CriticScore,
        Rating = m.Rating,
        Image = m.Image,
        Website = m.Website,
        Screenshots = m.Screenshots.Select(s => s.ToContract()).ToList(),
        Developers = m.Developers.Select(d => d.ToContract()).ToList(),
        Genres = m.Genres.Select(x => x.ToContract()).ToList()
    };

    public static ScreenshotContract ToContract(this MetadataScreenshot s) => new()
    {
        Id = s.Id,
        Image = s.Image,
        Width = s.Width,
        Height = s.Height,
        IsDeleted = s.IsDeleted
    };

    public static DeveloperContract ToContract(this MetadataDeveloper d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Slug = d.Slug
    };

    public static GenreContract ToContract(this MetadataGenre g) => new()
    {
        Id = g.Id,
        Name = g.Name,
        Slug = g.Slug
    };

    public static MetadataSearchResultContract ToContract(this ProviderSearchResult r) => new()
    {
        Provider = r.Provider,
        ExternalId = r.ExternalId,
        Name = r.Name,
        Slug = r.Slug,
        Released = r.Released,
        ThumbnailUrl = r.ThumbnailUrl
    };
}
