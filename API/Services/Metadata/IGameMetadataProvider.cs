namespace API.Services.Metadata;

/// <summary>
/// A source of game metadata.
///
/// Behind an interface for the same reason <c>IBoxArtProvider</c> is, and the reason is now a matter
/// of record rather than of taste: this application shipped against RAWG, RAWG stopped answering, and
/// the schema underneath was RAWG-shaped down to the column names. The interface plus the
/// provider-neutral tables are what make the next one a new implementation rather than another
/// migration.
///
/// It is NOT a plugin system. There is one implementation, it is chosen at registration, and nothing
/// picks between providers at runtime. The <see cref="Id"/> below is what gets stamped on stored rows
/// so an upgraded library can say which source a record came from.
/// </summary>
public interface IGameMetadataProvider
{
    /// <summary>Stamped on every row this provider produces. Matches MetadataGames.provider.</summary>
    string Id { get; }

    /// <summary>Shown in the UI: "IGDB", not "igdb".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether credentials are present. Read per call rather than cached — the admin settings page
    /// must be able to change them without a restart, which is the property that caching at
    /// construction has twice broken in this codebase.
    /// </summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    Task<ProviderSearchResponse> SearchAsync(
        string query, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Null when the provider has no such game. Throws only on transport failure.</summary>
    Task<ProviderGame?> GetAsync(int externalId, CancellationToken cancellationToken = default);
}
