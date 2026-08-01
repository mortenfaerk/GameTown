using EFModel.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Services.BoxArt;

/// <summary>What happened to a request to set box art, and why not if not.</summary>
public record BoxArtResult(bool Success, string? StoredPath, string Reason)
{
    public static BoxArtResult Failed(string reason) => new(false, null, reason);
    public static BoxArtResult Ok(string storedPath) => new(true, storedPath, "ok");
}

/// <summary>
/// Owns a game's box art: choosing it, storing it, replacing it and clearing it.
///
/// Every route in — a provider candidate, a pasted URL, an uploaded file — converges on one stored
/// form: a local file in the media directory, recorded as "/media/{guid}.{ext}". Nothing keeps a
/// remote URL. That mirrors what RAWG cover art and screenshots already do, for the same three
/// reasons: the library has to render on a LAN with no internet, a provider rotating its CDN must not
/// blank the shelf, and no third-party host should get a request for every visitor to the library.
/// </summary>
public class BoxArtService(
    DatabaseContext context,
    MediaStore media,
    ImageFetcher fetcher,
    IBoxArtProvider provider)
{
    /// <summary>Candidates for a title. Never throws for an ordinary failure — see IBoxArtProvider.</summary>
    public Task<BoxArtSearchResult> SearchAsync(string title, CancellationToken cancellationToken = default)
        => provider.SearchAsync(title, cancellationToken);

    /// <summary>
    /// Downloads an image and makes it the game's box art.
    ///
    /// The URL comes from a caller, so the download goes through <see cref="ImageFetcher"/> — which is
    /// where scheme, address, redirect, size and content-type are all enforced. Nothing here trusts
    /// the URL for anything, including the file extension.
    /// </summary>
    public async Task<BoxArtResult> SetFromUrlAsync(
        Guid gameId, string? url, CancellationToken cancellationToken = default)
    {
        var game = await FindAsync(gameId, cancellationToken);

        var fetched = await fetcher.FetchAsync(url, cancellationToken);
        if (!fetched.Success)
            return BoxArtResult.Failed(fetched.Reason);

        return await StoreAsync(game, fetched.Bytes, fetched.Extension, cancellationToken);
    }

    /// <summary>
    /// Makes an uploaded file the game's box art.
    ///
    /// The bytes are sniffed exactly as a downloaded image is. The browser's declared content-type and
    /// the file's own name are both discarded: these files are served back from the API's own origin,
    /// so taking a caller's word on what they are would be stored XSS.
    /// </summary>
    public async Task<BoxArtResult> SetFromUploadAsync(
        Guid gameId, Stream content, CancellationToken cancellationToken = default)
    {
        var game = await FindAsync(gameId, cancellationToken);

        // Bounded before anything is written: an unbounded copy would let one request fill the data
        // directory. The ceiling is the fetcher's, so the two paths cannot disagree about "too big".
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > ImageFetcher.MaxBytes)
                return BoxArtResult.Failed("too-large");
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        var extension = ImageFetcher.SniffExtension(bytes);

        return extension is null
            ? BoxArtResult.Failed("not-an-image")
            : await StoreAsync(game, bytes, extension, cancellationToken);
    }

    /// <summary>
    /// Removes the override, so the game falls back to its RAWG image again.
    ///
    /// The file is deleted rather than orphaned: a stored box art belongs to exactly one game and is
    /// written under a fresh GUID every time, so nothing else can be referencing it.
    /// </summary>
    public async Task ClearAsync(Guid gameId, CancellationToken cancellationToken = default)
    {
        var game = await FindAsync(gameId, cancellationToken);

        var superseded = game.BoxArtUrl;
        game.BoxArtUrl = null;
        await context.SaveChangesAsync(cancellationToken);

        media.Delete(superseded);
    }

    private async Task<GameTownGame> FindAsync(Guid gameId, CancellationToken cancellationToken)
        => await context.GameTownGames.FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken)
           ?? throw new KeyNotFoundException($"Game with ID {gameId} not found.");

    private async Task<BoxArtResult> StoreAsync(
        GameTownGame game, byte[] bytes, string extension, CancellationToken cancellationToken)
    {
        var stored = await media.WriteAsync(bytes, extension, cancellationToken);

        var superseded = game.BoxArtUrl;
        game.BoxArtUrl = stored;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // The file is on disk and nothing references it now. Same reasoning as the archive upload's
            // cleanup: an abandoned write silently consumes the disk this feature is meant to use
            // carefully.
            media.Delete(stored);
            throw;
        }

        // Only once the new path is committed. Deleting first would, on a failed save, leave the row
        // pointing at a file that no longer exists.
        media.Delete(superseded);

        return BoxArtResult.Ok(stored);
    }
}
