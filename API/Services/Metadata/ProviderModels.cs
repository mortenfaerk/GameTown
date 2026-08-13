namespace API.Services.Metadata;

/// <summary>
/// A game as a metadata provider describes it, before anything is stored.
///
/// Deliberately not an EF entity. The scaffolded <c>MetadataGame</c> is what is persisted, and going
/// straight to it was the shape that made <c>RAWGService</c> hard to reason about: a freshly
/// deserialised entity graph and a tracked one were the same type, so "is this attached?" had to be
/// carried in the reader's head. Here the boundary is in the type system — providers return this, and
/// only <see cref="GameMetadataService"/> turns it into rows.
///
/// Image members are still remote URLs at this point. They become local /media paths on the way in.
/// </summary>
public sealed record ProviderGame
{
    public required string Provider { get; init; }
    public required int ExternalId { get; init; }
    public required string Name { get; init; }
    public string Slug { get; init; } = string.Empty;

    /// <summary>HTML by the time it gets here — see <c>IgdbProvider</c> on encoding plain text.</summary>
    public string Description { get; init; } = string.Empty;

    public DateTime? Released { get; init; }

    /// <summary>0-100 critic aggregate. RAWG called it metacritic; IGDB calls it aggregated_rating.</summary>
    public double? CriticScore { get; init; }

    public double? Rating { get; init; }
    public string? ImageUrl { get; init; }
    public string Website { get; init; } = string.Empty;

    public List<ProviderCompany> Developers { get; init; } = [];
    public List<ProviderGenre> Genres { get; init; } = [];
    public List<ProviderScreenshot> Screenshots { get; init; } = [];
}

public sealed record ProviderCompany(int ExternalId, string? Name, string? Slug);

public sealed record ProviderGenre(int ExternalId, string? Name, string? Slug);

public sealed record ProviderScreenshot(int ExternalId, string ImageUrl, int Width, int Height);

/// <summary>
/// One row of a search result. Lighter than <see cref="ProviderGame"/> on purpose: the picker shows a
/// name, a year and a thumbnail, and fetching full records for twenty candidates to render that would
/// spend the rate limit on data nobody looks at.
/// </summary>
public sealed record ProviderSearchResult
{
    public required string Provider { get; init; }
    public required int ExternalId { get; init; }
    public required string Name { get; init; }
    public string Slug { get; init; } = string.Empty;
    public DateTime? Released { get; init; }

    /// <summary>Remote thumbnail URL, shown directly by the picker and never stored.</summary>
    public string? ThumbnailUrl { get; init; }
}

/// <summary>
/// The outcome of a provider call that can fail for reasons the UI must distinguish.
///
/// Same reasoning as <c>BoxArtSearchResult</c>: "no credentials are configured" and "that title is
/// not in the database" need different words on screen, and an empty list cannot tell them apart.
/// </summary>
public sealed record ProviderSearchResponse(List<ProviderSearchResult> Results, string Reason)
{
    public static ProviderSearchResponse Ok(List<ProviderSearchResult> results) => new(results, "ok");
    public static ProviderSearchResponse Failed(string reason) => new([], reason);
}
