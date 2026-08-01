namespace API.Services.BoxArt;

/// <summary>
/// A source of candidate box art for a game title.
///
/// An interface for one implementation, which is usually a smell. It earns its place here because the
/// choice of provider is genuinely open: Google's Custom Search JSON API — the only sanctioned way to
/// query Google Images — is closed to new users and is switched off on 1 January 2027, and Bing's
/// Image Search API was retired in August 2025. Whatever replaces SteamGridDB will plug in here
/// rather than being threaded back through the endpoints and the UI.
/// </summary>
public interface IBoxArtProvider
{
    /// <summary>Human-readable attribution, shown next to the results.</summary>
    string Name { get; }

    /// <summary>
    /// Candidates for a title, best first. Never throws for an ordinary failure — an unconfigured key,
    /// an unreachable provider or a title with no artwork all come back as a
    /// <see cref="BoxArtSearchResult"/> carrying a reason code, because all three are things the UI
    /// has to explain rather than things that have gone wrong.
    /// </summary>
    Task<BoxArtSearchResult> SearchAsync(string title, CancellationToken cancellationToken = default);
}
