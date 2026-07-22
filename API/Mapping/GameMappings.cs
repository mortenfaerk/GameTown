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

        // ACCEPTED RISK: HtmlSanitizer 9.0.967 hard-pins AngleSharp [0.17.1], which carries
        // CVE-2026-54570 — an mXSS flaw that can let crafted markup survive sanitisation. Fixed in
        // AngleSharp 1.5.0, currently only reachable via the HtmlSanitizer 9.1.x prerelease line,
        // which was declined. Half of that bug is unescaped '<'/'>' in serialised *attribute
        // values*, so no attribute is allowed through at all — with an empty allowlist there is no
        // attribute for it to bite on. Revisit when HtmlSanitizer 9.1.x goes stable.
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
        RawgGame = game.Rawggame?.ToContract()
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
        Released = g.Released?.ToDateTime(TimeOnly.MinValue),
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
