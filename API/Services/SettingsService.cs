using EFModel.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Services;

/// <summary>
/// Runtime-editable configuration, stored in the database and read on demand.
///
/// The point of reading on demand is that the admin settings page can change these without a
/// restart. Anything that caches a value at startup — the constructor arguments FileService and the
/// old RAWGService used to take — silently defeats that: the UI saves, the database updates, and the
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

    /// <summary>
    /// IGDB authenticates with a Twitch application, so the credential is a PAIR, not a key. The
    /// retired RAWGApiKey row is deleted by migration 007.
    /// </summary>
    public const string IgdbClientIdKey = "IGDBClientId";
    public const string IgdbClientSecretKey = "IGDBClientSecret";
    public const string AllowedFileTypesKey = "AllowedFileTypes";
    public const string MaxUploadSizeMbKey = "MaxUploadSizeMb";
    public const string BoxArtApiKeyKey = "BoxArtApiKey";

    /// <summary>
    /// The LAN Discord bot's Catalogue API. A base URL and a shared key, which is a pair in the same
    /// all-or-nothing sense as the IGDB credentials — see <see cref="GetLanBotCredentialsAsync"/>.
    /// </summary>
    public const string LanBotBaseUrlKey = "LanBotBaseUrl";
    public const string LanBotApiKeyKey = "LanBotApiKey";
    public const string LanBotSyncIntervalMinutesKey = "LanBotSyncIntervalMinutes";

    /// <summary>Where this install is reachable from, for links handed to anything outside it.</summary>
    public const string PublicBaseUrlKey = "PublicBaseUrl";

    /// <summary>Archive extensions accepted by the upload endpoint when nothing is configured.</summary>
    public static readonly string[] DefaultAllowedFileTypes =
        [".zip", ".7z", ".rar", ".tar", ".gz", ".iso"];

    /// <summary>
    /// No ceiling by default, which is what the application did before this setting existed — an
    /// upgrade must not start rejecting archives an install has been accepting for months.
    /// </summary>
    public const long DefaultMaxUploadSizeMb = 0;

    /// <summary>
    /// How often the LAN bot is polled. Fifteen minutes because suggestions arrive at conversational
    /// speed over days, not seconds — this is a list people add to in Discord between LANs, and a
    /// tighter interval would spend someone else's rate limit to learn nothing.
    /// </summary>
    public const int DefaultLanBotSyncIntervalMinutes = 15;

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
    /// The IGDB credential pair, or nulls when unset.
    ///
    /// Optional, as the RAWG key was: without credentials the app still works and metadata is entered
    /// by hand instead of imported. Both halves are returned together and both are null unless both
    /// are stored — a client id with no secret cannot authenticate, and treating it as configured
    /// would turn a clear "not set up" into a confusing failure at the first search.
    ///
    /// Read from the database on every call, with no cache. That is the rule the whole settings design
    /// rests on (see this class's summary), and it holds here even though the token derived from these
    /// values IS cached — see IgdbTokenProvider for how the two coexist.
    /// </summary>
    public async Task<(string? ClientId, string? ClientSecret)> GetIgdbCredentialsAsync()
    {
        var clientId = await GetRawAsync(IgdbClientIdKey);
        var clientSecret = await GetRawAsync(IgdbClientSecretKey);

        return clientId is null || clientSecret is null ? (null, null) : (clientId, clientSecret);
    }

    /// <summary>
    /// The client id on its own, whether or not a secret accompanies it.
    ///
    /// Only for display: the settings page shows which Twitch application an install is pointed at,
    /// including the half-configured state <see cref="GetIgdbCredentialsAsync"/> deliberately hides.
    /// Never use this to decide whether IGDB is usable.
    /// </summary>
    public Task<string?> GetIgdbClientIdAsync() => GetRawAsync(IgdbClientIdKey);

    /// <summary>
    /// The artwork provider's key (SteamGridDB), or null when unset.
    ///
    /// Optional in the same way the IGDB credentials are: without one the box-art *search* is unavailable and
    /// says so, while uploading a file or pasting an image URL keeps working. Losing the search is a
    /// smaller loss than being unable to set a cover at all, which is why the two paths do not share
    /// a dependency.
    /// </summary>
    public Task<string?> GetBoxArtApiKeyAsync() => GetRawAsync(BoxArtApiKeyKey);

    /// <summary>
    /// The LAN bot's base URL and API key, or nulls when either is missing.
    ///
    /// All-or-nothing for the same reason as <see cref="GetIgdbCredentialsAsync"/>: a base URL with
    /// no key cannot call anything, and reporting it as configured turns a clear "not set up" into a
    /// confusing failure at the first poll — which, for a background service, nobody is watching.
    ///
    /// Read per call, with no cache. <c>LanBotClient</c> must keep calling this rather than capturing
    /// the pair in its constructor; see this class's summary for the bug that rule exists to prevent.
    /// </summary>
    public async Task<(string? BaseUrl, string? ApiKey)> GetLanBotCredentialsAsync()
    {
        var baseUrl = await GetRawAsync(LanBotBaseUrlKey);
        var apiKey = await GetRawAsync(LanBotApiKeyKey);

        return baseUrl is null || apiKey is null ? (null, null) : (baseUrl, apiKey);
    }

    /// <summary>
    /// The base URL on its own, whether or not a key accompanies it. Display only — the same
    /// half-configured state <see cref="GetIgdbClientIdAsync"/> exposes, for the same reason.
    /// </summary>
    public Task<string?> GetLanBotBaseUrlAsync() => GetRawAsync(LanBotBaseUrlKey);

    /// <summary>
    /// Minutes between LAN bot polls. Zero means polling is off, which is a real choice and not the
    /// same as unconfigured: an operator may want the integration available for manual syncs only.
    ///
    /// An unparseable or negative stored value falls back to the default rather than to zero, so a
    /// hand-edited row cannot silently switch off a sync an operator believes is running.
    /// </summary>
    public async Task<int> GetLanBotSyncIntervalMinutesAsync()
    {
        var raw = await GetRawAsync(LanBotSyncIntervalMinutesKey);
        if (raw is null) return DefaultLanBotSyncIntervalMinutes;

        return int.TryParse(raw, out var minutes) && minutes >= 0
            ? minutes
            : DefaultLanBotSyncIntervalMinutes;
    }

    /// <summary>
    /// Where this install is reachable from, e.g. "http://10.0.0.5:5187". Null when unset.
    ///
    /// Nothing inside GameTown needs this — the SPA resolves its own API address from wherever it was
    /// loaded, which is what lets one published artifact run at any address. It exists for the one
    /// thing that cannot work that out: something OUTSIDE this install that wants to link back into
    /// it. Today that is the LAN bot, which composes its links from its own configuration; this is
    /// sent alongside every call so it need not, and is what the LAN screen shows an operator so they
    /// can hand over the exact deep link.
    ///
    /// Stored without a trailing slash so callers can concatenate a rooted path.
    /// </summary>
    public async Task<string?> GetPublicBaseUrlAsync()
    {
        var raw = await GetRawAsync(PublicBaseUrlKey);
        return raw?.TrimEnd('/');
    }

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
