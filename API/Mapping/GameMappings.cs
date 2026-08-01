using API.Services.Archives;
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
    /// RAWG descriptions are HTML by design, and the client renders them with MarkupString, which
    /// bypasses Blazor's encoding. RAWG is a community-editable database, so that string is
    /// untrusted: without this an entry could carry script into the public game page.
    ///
    /// Sanitising here rather than on ingest means rows already stored unsanitised are cleaned on
    /// the way out, with no migration. Configure once — Sanitize() is safe to call concurrently.
    /// </summary>
    private static readonly HtmlSanitizer DescriptionSanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();

        // Formatting tags only. No <a>: RAWG descriptions gain little from links here, and dropping
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

    public static GameContract ToContract(this GameTownGame game) => new()
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
        RawgGame = game.Rawggame?.ToContract()
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

    public static RawgGameContract ToContract(this Rawggame g) => new()
    {
        Id = g.Id,
        Slug = g.Slug,
        Name = g.Name,
        NameOriginal = g.NameOriginal,
        Description = Sanitize(g.Description),
        Metacritic = g.Metacritic,
        // Npgsql maps the `date` column to DateOnly; the contract keeps DateTime? so the
        // JSON shape is unchanged for existing clients.
        // SQLite scaffolds `date` to DateTime?, where Npgsql gave DateOnly?. The contract
        // has always been DateTime?, so this is now a straight copy.
        Released = g.Released,
        Tba = g.Tba,
        Updated = g.Updated,
        BackgroundImage = g.BackgroundImage,
        BackgroundImageAdditional = g.BackgroundImageAdditional,
        Website = g.Website,
        Rating = g.Rating,
        RatingTop = g.RatingTop,
        Playtime = g.Playtime,
        ScreenshotsCount = g.ScreenshotsCount,
        MoviesCount = g.MoviesCount,
        CreatorsCount = g.CreatorsCount,
        AchievementsCount = g.AchievementsCount,
        ParentAchievementsCount = g.ParentAchievementsCount,
        RedditUrl = g.RedditUrl,
        RedditCount = g.RedditCount,
        TwitchCount = g.TwitchCount,
        YoutubeCount = g.YoutubeCount,
        ReviewsTextCount = g.ReviewsTextCount,
        RatingsCount = g.RatingsCount,
        SuggestionsCount = g.SuggestionsCount,
        MetacriticUrl = g.MetacriticUrl,
        ParentsCount = g.ParentsCount,
        AdditionsCount = g.AdditionsCount,
        GameSeriesCount = g.GameSeriesCount,
        ReviewsCount = g.ReviewsCount,
        SaturatedColor = g.SaturatedColor,
        DominantColor = g.DominantColor,
        Screenshots = g.Screenshots.Select(s => s.ToContract()).ToList(),
        Developers = g.Developers.Select(d => d.ToContract()).ToList(),
        Genres = g.Genres.Select(x => x.ToContract()).ToList()
    };

    public static ScreenshotContract ToContract(this Rawgscreenshot s) => new()
    {
        Id = s.Id,
        Image = s.Image,
        Width = s.Width,
        Height = s.Height,
        IsDeleted = s.IsDeleted
    };

    public static DeveloperContract ToContract(this Rawgdeveloper d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Slug = d.Slug,
        GamesCount = d.GamesCount,
        ImageBackground = d.ImageBackground
    };

    public static GenreContract ToContract(this Rawggenre g) => new()
    {
        Id = g.Id,
        Name = g.Name,
        Slug = g.Slug,
        ImageBackground = g.ImageBackground
    };
}
