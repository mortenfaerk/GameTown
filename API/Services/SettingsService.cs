using EFModel.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Services;

/// <summary>
/// Runtime-editable configuration, stored in the database and read on demand.
///
/// The point of reading on demand is that the admin settings page can change these without a
/// restart. Anything that caches a value at startup — the constructor arguments FileService and
/// RAWGService used to take — silently defeats that: the UI saves, the database updates, and the
/// running service keeps using the value it captured at boot.
///
/// There is deliberately **no cache**. At this scale a settings read is a primary-key lookup against
/// a local file, and a cache here would need invalidation reachable from the endpoint that writes.
/// A scoped cache in particular would give each request its own stale copy, which presents as the
/// save having silently failed. If profiling ever justifies one, it belongs behind an explicit
/// invalidation call, not a lazily-populated dictionary.
///
/// Defaults live here rather than in seed rows, so a missing key means "use the default" and a
/// partially-populated table cannot leave the app unbootable.
/// </summary>
public class SettingsService(DatabaseContext dbContext, string dataDirectory)
{
    public const string GameFilesPathKey = "GameFilesPath";
    public const string RawgApiKeyKey = "RAWGApiKey";
    public const string AllowedFileTypesKey = "AllowedFileTypes";
    public const string MaxUploadSizeMbKey = "MaxUploadSizeMb";
    public const string BoxArtApiKeyKey = "BoxArtApiKey";

    /// <summary>Archive extensions accepted by the upload endpoint when nothing is configured.</summary>
    public static readonly string[] DefaultAllowedFileTypes =
        [".zip", ".7z", ".rar", ".tar", ".gz", ".iso"];

    /// <summary>
    /// No ceiling by default, which is what the application did before this setting existed — an
    /// upgrade must not start rejecting archives an install has been accepting for months.
    /// </summary>
    public const long DefaultMaxUploadSizeMb = 0;

    public string DataDirectory => dataDirectory;

    private async Task<string?> GetRawAsync(string key)
    {
        var row = await dbContext.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        return string.IsNullOrWhiteSpace(row?.Value) ? null : row.Value;
    }

    /// <summary>
    /// Where uploaded game archives are written. Defaults inside the data directory, which is the
    /// only location an in-place upgrade does not overwrite.
    /// </summary>
    public async Task<string> GetGameFilesPathAsync()
        => await GetRawAsync(GameFilesPathKey) ?? Path.Combine(dataDirectory, "games");

    /// <summary>
    /// Where re-hosted cover art and screenshots are written.
    ///
    /// Derived from the data directory and deliberately NOT a setting. Static file serving binds its
    /// file provider once at startup, so a "live-editable" media path would be a setting that
    /// appears to save and then does nothing until a restart — worse than not offering it. Uploaded
    /// archives are the location people actually want to move (onto a big disk), and GameFilesPath
    /// is editable because FileService resolves it per operation with no middleware involved.
    ///
    /// This used to be the application's own wwwroot/media, which an in-place upgrade deletes along
    /// with the rest of the app folder — silently emptying every cover in the library.
    /// </summary>
    public string MediaDirectory => MediaDirectoryFor(dataDirectory);

    /// <summary>Startup needs this before any scoped service exists, hence the static form.</summary>
    public static string MediaDirectoryFor(string dataDirectory)
        => Path.Combine(dataDirectory, "media");

    /// <summary>
    /// Null when unset. RAWG is optional: without a key the app still works, metadata is just
    /// entered by hand instead of imported.
    /// </summary>
    public Task<string?> GetRawgApiKeyAsync() => GetRawAsync(RawgApiKeyKey);

    /// <summary>
    /// The artwork provider's key (SteamGridDB), or null when unset.
    ///
    /// Optional in the same way the RAWG key is: without one the box-art *search* is unavailable and
    /// says so, while uploading a file or pasting an image URL keeps working. Losing the search is a
    /// smaller loss than being unable to set a cover at all, which is why the two paths do not share
    /// a dependency.
    /// </summary>
    public Task<string?> GetBoxArtApiKeyAsync() => GetRawAsync(BoxArtApiKeyKey);

    public async Task<string[]> GetAllowedFileTypesAsync()
    {
        var raw = await GetRawAsync(AllowedFileTypesKey);
        if (raw is null) return DefaultAllowedFileTypes;

        var parsed = ParseFileTypes(raw);
        // An empty list would accept nothing at all and make uploading impossible, which is a worse
        // failure than falling back. Treat "configured to nothing" as "not configured".
        return parsed.Length == 0 ? DefaultAllowedFileTypes : parsed;
    }

    /// <summary>
    /// Largest archive a contributor may upload, in megabytes. Zero means no ceiling.
    ///
    /// A stored value that will not parse is treated as unset rather than as zero, so a hand-edited
    /// row cannot silently remove the limit an operator thought they had set.
    /// </summary>
    public async Task<long> GetMaxUploadSizeMbAsync()
    {
        var raw = await GetRawAsync(MaxUploadSizeMbKey);
        if (raw is null) return DefaultMaxUploadSizeMb;

        return long.TryParse(raw, out var megabytes) && megabytes >= 0
            ? megabytes
            : DefaultMaxUploadSizeMb;
    }

    /// <summary>
    /// The same limit in bytes, or null when there is no limit.
    ///
    /// Null rather than long.MaxValue because that is what
    /// <c>IHttpMaxRequestBodySizeFeature.MaxRequestBodySize</c> wants for "unlimited", and the
    /// upload reader uses the same convention.
    /// </summary>
    public async Task<long?> GetMaxUploadSizeBytesAsync()
    {
        var megabytes = await GetMaxUploadSizeMbAsync();
        return megabytes <= 0 ? null : megabytes * 1024L * 1024L;
    }

    /// <summary>
    /// Normalises a comma-separated extension list: trims, lowercases, and forces a leading dot so
    /// "ZIP, .7z" and ".zip,.7z" mean the same thing.
    /// </summary>
    public static string[] ParseFileTypes(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
           .Where(e => e.Length > 1)
           .Distinct()
           .ToArray();

    /// <summary>
    /// Writes a setting. A null or blank value deletes the row, which restores the coded default —
    /// that is what makes "clear this field" mean "go back to the default" rather than "set it to
    /// empty and break".
    /// </summary>
    public async Task SetAsync(string key, string? value)
    {
        var row = await dbContext.Settings.FirstOrDefaultAsync(s => s.Key == key);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (row is not null) dbContext.Settings.Remove(row);
        }
        else if (row is null)
        {
            dbContext.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }

        await dbContext.SaveChangesAsync();
    }
}
