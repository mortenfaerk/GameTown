using EFModel.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Services.Metadata;

/// <summary>
/// Turns what a provider returned into stored rows, and re-hosts its images on the way.
///
/// The successor to <c>RAWGService.EnsureRawgGamePersisted</c>, and it keeps that method's hard-won
/// discipline: metadata rows are SHARED between GameTown games (two uploads of the same title point
/// at one record, and several titles share a studio or a genre), so a freshly fetched graph can never
/// be attached wholesale. Every related row is resolved against what is already stored or already
/// tracked first, or EF issues an INSERT with an existing key the moment a second game references the
/// same studio.
///
/// What changed is the key. RAWG rows used RAWG's id as the primary key; these use a surrogate, with
/// the provider's id in <c>ExternalId</c>. So "have I seen this before?" is a lookup on
/// (provider, external id) rather than on the key, and two providers can hold the same integer id
/// without colliding — which is the whole reason the surrogate exists.
/// </summary>
public class GameMetadataService(
    DatabaseContext context,
    IGameMetadataProvider provider,
    MediaStore media,
    ImageFetcher fetcher,
    ILogger<GameMetadataService> logger)
{
    public IGameMetadataProvider Provider => provider;

    /// <summary>
    /// Fetches a game from the provider and returns it as a <b>tracked</b> entity, inserting it the
    /// first time and refreshing it afterwards.
    ///
    /// The caller owns SaveChangesAsync, so this can take part in a larger unit of work — adding a
    /// game writes the metadata and the library row in one transaction.
    /// </summary>
    public async Task<MetadataGame> EnsurePersistedAsync(
        int externalId, CancellationToken cancellationToken = default)
    {
        var fetched = await provider.GetAsync(externalId, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Game with ID {externalId} was not found by {provider.DisplayName}.");

        return await PersistAsync(fetched, cancellationToken);
    }

    /// <summary>
    /// Stores an already-fetched provider record. Split from <see cref="EnsurePersistedAsync"/> so the
    /// re-link tool can fetch candidates once, show them, and store only the one an admin confirms —
    /// without a second round trip against the rate limit.
    /// </summary>
    public async Task<MetadataGame> PersistAsync(
        ProviderGame fetched, CancellationToken cancellationToken = default)
    {
        var existing = await context.MetadataGames
            .Include(m => m.Developers)
            .Include(m => m.Genres)
            .Include(m => m.Screenshots)
            .FirstOrDefaultAsync(
                m => m.Provider == fetched.Provider && m.ExternalId == fetched.ExternalId,
                cancellationToken);

        // Held before the re-host overwrites it, so a refresh that downloaded a new cover can bin the
        // one it replaced. Skipping this leaves an orphan in the media directory per refresh.
        var supersededImage = existing?.Image;

        var image = await RehostAsync(fetched.ImageUrl, cancellationToken) ?? existing?.Image;

        var developers = await ResolveDevelopersAsync(fetched, cancellationToken);
        var genres = await ResolveGenresAsync(fetched, cancellationToken);
        var screenshots = await ResolveScreenshotsAsync(fetched, cancellationToken);

        if (existing is null)
        {
            var created = new MetadataGame
            {
                Provider = fetched.Provider,
                ExternalId = fetched.ExternalId,
                Slug = fetched.Slug,
                Name = fetched.Name,
                Description = fetched.Description,
                Released = fetched.Released,
                CriticScore = fetched.CriticScore,
                Rating = fetched.Rating,
                Image = image,
                Website = fetched.Website,
                Updated = DateTime.UtcNow,
                Developers = developers,
                Genres = genres,
                Screenshots = screenshots
            };

            context.MetadataGames.Add(created);
            return created;
        }

        // Only when it actually changed: a refresh that re-hosted nothing leaves both pointing at the
        // same file, and deleting it would blank the cover it was meant to preserve.
        if (supersededImage != image) media.Delete(supersededImage);

        existing.Slug = fetched.Slug;
        existing.Name = fetched.Name;
        existing.Description = fetched.Description;
        existing.Released = fetched.Released;
        existing.CriticScore = fetched.CriticScore;
        existing.Rating = fetched.Rating;
        existing.Image = image;
        existing.Website = fetched.Website;
        existing.Updated = DateTime.UtcNow;

        Merge(existing.Developers, developers, d => d.Id);
        Merge(existing.Genres, genres, g => g.Id);
        Merge(existing.Screenshots, screenshots, s => s.Id);

        return existing;
    }

    /// <summary>Adds links that are not already there, leaving existing ones alone.</summary>
    private static void Merge<T>(ICollection<T> current, List<T> incoming, Func<T, int> keyOf)
    {
        foreach (var item in incoming)
        {
            if (!current.Any(existing => keyOf(existing) == keyOf(item)))
                current.Add(item);
        }
    }

    private async Task<List<MetadataDeveloper>> ResolveDevelopersAsync(
        ProviderGame fetched, CancellationToken cancellationToken)
    {
        var items = fetched.Developers.GroupBy(d => d.ExternalId).Select(g => g.First()).ToList();
        if (items.Count == 0) return [];

        var ids = items.Select(d => d.ExternalId).ToList();
        var stored = await context.MetadataDevelopers
            .Where(d => d.Provider == fetched.Provider && ids.Contains(d.ExternalId))
            .ToListAsync(cancellationToken);

        var resolved = new List<MetadataDeveloper>();
        foreach (var item in items)
        {
            // Local as well as stored: within one unit of work the same studio can be resolved twice
            // (two games by the same developer added together), and the second lookup would miss the
            // row the first one added but has not saved.
            var match = stored.FirstOrDefault(d => d.ExternalId == item.ExternalId)
                        ?? context.MetadataDevelopers.Local.FirstOrDefault(
                            d => d.Provider == fetched.Provider && d.ExternalId == item.ExternalId);

            if (match is null)
            {
                var created = new MetadataDeveloper
                {
                    Provider = fetched.Provider,
                    ExternalId = item.ExternalId,
                    Name = item.Name,
                    Slug = item.Slug
                };
                context.MetadataDevelopers.Add(created);
                resolved.Add(created);
            }
            else
            {
                match.Name = item.Name;
                match.Slug = item.Slug;
                resolved.Add(match);
            }
        }
        return resolved;
    }

    private async Task<List<MetadataGenre>> ResolveGenresAsync(
        ProviderGame fetched, CancellationToken cancellationToken)
    {
        var items = fetched.Genres.GroupBy(g => g.ExternalId).Select(g => g.First()).ToList();
        if (items.Count == 0) return [];

        var ids = items.Select(g => g.ExternalId).ToList();
        var stored = await context.MetadataGenres
            .Where(g => g.Provider == fetched.Provider && ids.Contains(g.ExternalId))
            .ToListAsync(cancellationToken);

        var resolved = new List<MetadataGenre>();
        foreach (var item in items)
        {
            var match = stored.FirstOrDefault(g => g.ExternalId == item.ExternalId)
                        ?? context.MetadataGenres.Local.FirstOrDefault(
                            g => g.Provider == fetched.Provider && g.ExternalId == item.ExternalId);

            if (match is null)
            {
                var created = new MetadataGenre
                {
                    Provider = fetched.Provider,
                    ExternalId = item.ExternalId,
                    Name = item.Name,
                    Slug = item.Slug
                };
                context.MetadataGenres.Add(created);
                resolved.Add(created);
            }
            else
            {
                match.Name = item.Name;
                match.Slug = item.Slug;
                resolved.Add(match);
            }
        }
        return resolved;
    }

    private async Task<List<MetadataScreenshot>> ResolveScreenshotsAsync(
        ProviderGame fetched, CancellationToken cancellationToken)
    {
        var items = fetched.Screenshots.GroupBy(s => s.ExternalId).Select(g => g.First()).ToList();
        if (items.Count == 0) return [];

        var ids = items.Select(s => s.ExternalId).ToList();
        var stored = await context.MetadataScreenshots
            .Where(s => s.Provider == fetched.Provider && ids.Contains(s.ExternalId))
            .ToListAsync(cancellationToken);

        var resolved = new List<MetadataScreenshot>();
        foreach (var item in items)
        {
            var match = stored.FirstOrDefault(s => s.ExternalId == item.ExternalId)
                        ?? context.MetadataScreenshots.Local.FirstOrDefault(
                            s => s.Provider == fetched.Provider && s.ExternalId == item.ExternalId);

            if (match is not null)
            {
                // Already stored, and its Image already points at a re-hosted copy. Left alone so a
                // refresh does not re-download every screenshot and churn the media directory.
                resolved.Add(match);
                continue;
            }

            var rehosted = await RehostAsync(item.ImageUrl, cancellationToken);
            if (rehosted is null) continue;

            var created = new MetadataScreenshot
            {
                Provider = fetched.Provider,
                ExternalId = item.ExternalId,
                Image = rehosted,
                Width = item.Width,
                Height = item.Height,
                IsDeleted = false
            };
            context.MetadataScreenshots.Add(created);
            resolved.Add(created);
        }
        return resolved;
    }

    /// <summary>
    /// Downloads a remote image into the media directory and returns its local "/media/{guid}.ext"
    /// path, so the library keeps working on a LAN with no internet — and, as this migration made
    /// vivid, keeps working after the provider itself stops existing.
    ///
    /// Through <see cref="ImageFetcher"/>, never a bare HttpClient. That it is now a fixed, known host
    /// (images.igdb.com) rather than a community-editable URL changes nothing: the fetcher refuses
    /// non-HTTP schemes, refuses redirects, connects only to public addresses, caps the body, and
    /// derives the extension by sniffing magic bytes rather than trusting the URL. These files are
    /// served back from this API's own origin, so an accepted SVG or HTML document would be stored
    /// XSS — and "we built the URL ourselves" says nothing about the bytes at the far end of it.
    ///
    /// Returns null on failure, and callers keep whatever they had.
    /// </summary>
    private async Task<string?> RehostAsync(string? remoteUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || MediaStore.IsLocal(remoteUrl))
            return remoteUrl;

        var fetched = await fetcher.FetchAsync(remoteUrl, cancellationToken);
        if (!fetched.Success)
        {
            logger.LogWarning("Failed to download image: {Reason}", fetched.Reason);
            return null;
        }

        return await media.WriteAsync(fetched.Bytes, fetched.Extension, cancellationToken);
    }
}
