namespace GameTown.Contracts.Games;

/// <summary>
/// Provider metadata for a game, as re-served by the GameTown API.
///
/// Replaces RawgGameContract, and is deliberately much smaller than it was. That contract carried
/// every column RAWG returned — reddit_count, twitch_count, suggestions_count, saturated_color and a
/// dozen more — none of which any screen ever rendered. They are not carried forward.
/// </summary>
public class GameMetadataContract
{
    /// <summary>Local surrogate id. Meaningful only to this install.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Which source this record came from: "rawg" for a library carried across by migration 007,
    /// "igdb" for anything fetched since.
    ///
    /// On the wire because the UI genuinely needs it: the detail page hides "refresh metadata" for a
    /// retired provider (there is nothing to refresh from), and the admin re-link screen works from
    /// exactly this field.
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's own id, which is what its website and API address the game by.</summary>
    public int ExternalId { get; set; }

    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>HTML, already sanitised by the server. Rendered with MarkupString.</summary>
    public string Description { get; set; } = string.Empty;

    public DateTime? Released { get; set; }

    /// <summary>
    /// A 0-100 critic aggregate: RAWG's Metacritic score on carried rows, IGDB's aggregated_rating on
    /// new ones. Fractional, because IGDB's is — the UI rounds it.
    ///
    /// Named for what it is rather than "Metacritic", which it is not on any row written after this
    /// migration. Labelling an IGDB aggregate as Metacritic would be a small lie that gets believed.
    /// </summary>
    public double? CriticScore { get; set; }

    public double? Rating { get; set; }

    /// <summary>The main image, as "/media/{guid}.ext". Always local.</summary>
    public string? Image { get; set; }

    public string Website { get; set; } = string.Empty;

    public List<ScreenshotContract> Screenshots { get; set; } = [];
    public List<DeveloperContract> Developers { get; set; } = [];
    public List<GenreContract> Genres { get; set; } = [];
}

/// <summary>
/// One candidate from a metadata search, for the picker.
///
/// Lighter than the full record: the picker shows a name, a year and a thumbnail, and fetching
/// complete records for twenty candidates would spend the provider's rate limit on data nobody looks
/// at. The full record is fetched when one is chosen.
/// </summary>
public class MetadataSearchResultContract
{
    public string Provider { get; set; } = string.Empty;
    public int ExternalId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public DateTime? Released { get; set; }

    /// <summary>
    /// A remote URL at the provider's CDN, not a local path — nothing is downloaded until a candidate
    /// is actually chosen. Search results are browsed and discarded; re-hosting every thumbnail
    /// anyone scrolls past would fill the media directory with art for games nobody added.
    /// </summary>
    public string? ThumbnailUrl { get; set; }
}

/// <summary>
/// The outcome of a metadata search.
///
/// A wrapper rather than a bare list, for the same reason <see cref="BoxArtSearchResult"/> is one:
/// "no credentials are configured" and "that title is not in the database" need different words on
/// screen, and an empty array cannot tell them apart.
/// </summary>
public class MetadataSearchResponse
{
    public List<MetadataSearchResultContract> Results { get; set; } = [];

    /// <summary>
    /// A fixed code, never an exception message: "ok", "not-configured", "rejected", "rate-limited"
    /// or "unreachable".
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Which provider answered, for attribution in the picker ("IGDB").</summary>
    public string ProviderName { get; set; } = string.Empty;
}
