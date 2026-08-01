using EFModel.Models;
using RestSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using API.Models.Games;
using API.Models.Games.RAWGData;
using Microsoft.EntityFrameworkCore;
namespace API.Services;

public class RAWGService(SettingsService _settings, DatabaseContext _context)
{
    /// <summary>
    /// The RAWG key, read per call rather than captured at construction so the settings page can
    /// change it without a restart. Throws when unset: RAWG is optional overall — the app runs and
    /// games can be added by hand — but a call that has already reached this point cannot proceed
    /// without a key, and failing here is clearer than sending an unauthenticated request.
    /// </summary>
    private async Task<string> GetApiKeyAsync()
        => await _settings.GetRawgApiKeyAsync()
           ?? throw new InvalidOperationException(
               "No RAWG API key is configured. Set one under Administer -> Settings, or add games manually.");
    private DatabaseContext context = _context;

    /// <summary>
    /// RAWG serves snake_case. Newtonsoft matches property names case-insensitively but does NOT
    /// strip underscores, so without this every snake_case field silently deserialised to null —
    /// which is why games had no cover art (background_image) and developers no games_count.
    /// The entities are scaffolded and must not be annotated by hand, so the mapping lives here.
    /// </summary>
    private static readonly JsonSerializerSettings RawgJson = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() }
    };

    public async Task<Rawggame?> GetGameById(string id)
    {
        var client = new RestClient();
        var request = new RestRequest($"https://api.rawg.io/api/games/{id}", Method.Get).AddParameter("key", await GetApiKeyAsync());
        RestResponse response = await client.ExecuteAsync(request);

        if (response.Content == null)
            return null;
        Rawggame? result = JsonConvert.DeserializeObject<Rawggame?>(response.Content, RawgJson);
        return result;
    }
    public async Task<List<Rawgscreenshot>> GetGameScreenshots(string id, int page = 1, int pageSize = 20)
    {
        var client = new RestClient();
        var request = new RestRequest($"https://api.rawg.io/api/games/{id}/screenshots", Method.Get)
            .AddParameter("key", await GetApiKeyAsync())
            .AddParameter("page", page)
            .AddParameter("page_size", pageSize);

        List<Rawgscreenshot> screenshots = new();
        RestResponse response = await client.ExecuteAsync(request);
        if (response.Content == null)
            return screenshots;

        var result = JsonConvert.DeserializeObject<RawgScreenshotsResponse>(response.Content, RawgJson);
        if (result?.Results == null)
            return screenshots;

        int foundCount = result.Count;
        screenshots = result.Results.ToList();

        // `result?` rather than `result`: the loop body reassigns it from a deserialise that can
        // return null, and the next iteration's condition would then throw. The guard below only
        // skips the AddRange, so an unparseable page part-way through pagination — RAWG returning an
        // error body, say — used to take the whole request down with a NullReferenceException.
        while (result?.Next != null && screenshots.Count < foundCount)
        {
            request = new RestRequest(result.Next, Method.Get).AddParameter("key", await GetApiKeyAsync());
            response = await client.ExecuteAsync(request);
            if (response.Content == null) break;

            result = JsonConvert.DeserializeObject<RawgScreenshotsResponse>(response.Content, RawgJson);
            if (result?.Results != null)
                screenshots.AddRange(result.Results);
        }

        // Returns RAWG's own URLs. Re-hosting happens in ResolveScreenshotsAsync, and only for
        // screenshots we have not stored before — downloading here would re-fetch the whole set on
        // every metadata refresh and orphan the previous copies in wwwroot/media.
        return screenshots;
    }
    public async Task<List<RawgGameContract>> SearchGames(string query, int page = 1, int pageSize = 20)
    {
        var client = new RestClient();
        var request = new RestRequest("https://api.rawg.io/api/games", Method.Get)
            .AddParameter("key", await GetApiKeyAsync())
            .AddParameter("page", page)
            .AddParameter("page_size", pageSize)
            .AddParameter("search", query);
        
        RestResponse response = await client.ExecuteAsync(request);
        if (response.Content == null)
            return new List<RawgGameContract>();
        
        var result = JsonConvert.DeserializeObject<RAWGSearchResponse>(response.Content, RawgJson);
        List<RawgGameContract> games = new();
        if (result?.Results != null)
        {
            foreach (var game in result.Results)
            {
                games.Add(game.ToContract());
            }
        }
        return games;
    }
    /// <summary>
    /// Fetches a game from RAWG and returns it as a <b>tracked</b> entity, inserting it if this is
    /// the first time we have seen it and refreshing it otherwise.
    ///
    /// RAWG ids are the primary keys, and games share developers, genres and screenshots. Attaching
    /// a freshly deserialised graph unconditionally therefore makes EF issue an INSERT with an
    /// existing key as soon as a second GameTown game references the same RAWG title (or merely the
    /// same studio). Every related row is resolved against what is already stored or tracked first.
    ///
    /// The caller is responsible for SaveChangesAsync so this can take part in a larger unit of work.
    /// </summary>
    public async Task<Rawggame> EnsureRawgGamePersisted(string id)
    {
        var fetched = await GetGameById(id)
            ?? throw new KeyNotFoundException($"Game with ID {id} not found in RAWG API.");

        var existing = await context.Rawggames
            .Include(g => g.Developers)
            .Include(g => g.Genres)
            .Include(g => g.Screenshots)
            .FirstOrDefaultAsync(g => g.Id == fetched.Id);

        // Cover art gets the same treatment as screenshots: pulled onto this server so the library
        // renders with no internet, and so it survives RAWG rotating its CDN URLs.
        var supersededCover = existing?.BackgroundImage;
        var supersededCoverAdditional = existing?.BackgroundImageAdditional;

        fetched.BackgroundImage = await RehostImageAsync(fetched.BackgroundImage) ?? fetched.BackgroundImage;
        fetched.BackgroundImageAdditional = await RehostImageAsync(fetched.BackgroundImageAdditional) ?? fetched.BackgroundImageAdditional;

        var developers = await ResolveDevelopersAsync(fetched.Developers);
        var genres = await ResolveGenresAsync(fetched.Genres);
        var screenshots = await ResolveScreenshotsAsync(await GetGameScreenshots(id));

        if (existing is null)
        {
            fetched.Developers = developers;
            fetched.Genres = genres;
            fetched.Screenshots = screenshots;
            context.Rawggames.Add(fetched);
            return fetched;
        }

        // Refreshing re-downloads the cover under a new name, so bin the copy it replaces —
        // otherwise every refresh would leave another orphan in wwwroot/media.
        DeleteSupersededMedia(supersededCover, fetched.BackgroundImage);
        DeleteSupersededMedia(supersededCoverAdditional, fetched.BackgroundImageAdditional);

        // Copy scalars only: the navigations on `fetched` are untracked duplicates, and letting
        // SetValues near them would re-introduce the duplicate-key problem.
        fetched.Developers = [];
        fetched.Genres = [];
        fetched.Screenshots = [];
        context.Entry(existing).CurrentValues.SetValues(fetched);

        Merge(existing.Developers, developers, d => d.Id);
        Merge(existing.Genres, genres, g => g.Id);
        Merge(existing.Screenshots, screenshots, s => s.Id);
        return existing;
    }

    /// <summary>Removes a locally re-hosted file that a refresh has just replaced.</summary>
    private void DeleteSupersededMedia(string? previousPath, string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(previousPath)
            || !previousPath.StartsWith("/media/")
            || previousPath == currentPath)
        {
            return;
        }

        try
        {
            var mediaRoot = _settings.MediaDirectory;
            var fileName = Path.GetFileName(previousPath);
            var path = Path.Combine(mediaRoot, fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to remove superseded media {previousPath}: {ex.Message}");
        }
    }

    /// <summary>Adds any related rows that are not already linked, leaving existing links alone.</summary>
    private static void Merge<T>(ICollection<T> current, List<T> incoming, Func<T, int> keyOf)
    {
        foreach (var item in incoming)
        {
            if (!current.Any(existing => keyOf(existing) == keyOf(item)))
                current.Add(item);
        }
    }

    private async Task<List<Rawgdeveloper>> ResolveDevelopersAsync(ICollection<Rawgdeveloper> incoming)
    {
        var items = incoming.GroupBy(d => d.Id).Select(g => g.First()).ToList();
        if (items.Count == 0) return [];

        var ids = items.Select(d => d.Id).ToList();
        var stored = await context.Rawgdevelopers.Where(d => ids.Contains(d.Id)).ToListAsync();

        var resolved = new List<Rawgdeveloper>();
        foreach (var item in items)
        {
            var match = stored.FirstOrDefault(d => d.Id == item.Id)
                        ?? context.Rawgdevelopers.Local.FirstOrDefault(d => d.Id == item.Id);
            if (match is null)
            {
                context.Rawgdevelopers.Add(item);
                resolved.Add(item);
            }
            else
            {
                // Refresh the stored row's scalars, otherwise a metadata refresh would leave
                // rows written before the snake_case fix stuck with null games_count/image_background.
                context.Entry(match).CurrentValues.SetValues(item);
                resolved.Add(match);
            }
        }
        return resolved;
    }

    private async Task<List<Rawggenre>> ResolveGenresAsync(ICollection<Rawggenre> incoming)
    {
        var items = incoming.GroupBy(g => g.Id).Select(g => g.First()).ToList();
        if (items.Count == 0) return [];

        var ids = items.Select(g => g.Id).ToList();
        var stored = await context.Rawggenres.Where(g => ids.Contains(g.Id)).ToListAsync();

        var resolved = new List<Rawggenre>();
        foreach (var item in items)
        {
            var match = stored.FirstOrDefault(g => g.Id == item.Id)
                        ?? context.Rawggenres.Local.FirstOrDefault(g => g.Id == item.Id);
            if (match is null)
            {
                context.Rawggenres.Add(item);
                resolved.Add(item);
            }
            else
            {
                context.Entry(match).CurrentValues.SetValues(item);
                resolved.Add(match);
            }
        }
        return resolved;
    }

    private async Task<List<Rawgscreenshot>> ResolveScreenshotsAsync(ICollection<Rawgscreenshot> incoming)
    {
        var items = incoming.GroupBy(s => s.Id).Select(g => g.First()).ToList();
        if (items.Count == 0) return [];

        var ids = items.Select(s => s.Id).ToList();
        var stored = await context.Rawgscreenshots.Where(s => ids.Contains(s.Id)).ToListAsync();

        var resolved = new List<Rawgscreenshot>();
        foreach (var item in items)
        {
            var match = stored.FirstOrDefault(s => s.Id == item.Id)
                        ?? context.Rawgscreenshots.Local.FirstOrDefault(s => s.Id == item.Id);
            if (match is null)
            {
                item.Image = await RehostImageAsync(item.Image) ?? item.Image;
                context.Rawgscreenshots.Add(item);
                resolved.Add(item);
            }
            else
            {
                // Already stored, and its Image already points at a re-hosted /media/ copy.
                // Leave it alone so refreshes do not churn the media folder.
                resolved.Add(match);
            }
        }
        return resolved;
    }
    /// <summary>
    /// Downloads a remote image into the configured media directory and returns its local
    /// "/media/{guid}.ext" path,
    /// so the library keeps working on a LAN with no internet. Returns null if the download fails
    /// (callers keep the original URL). Already-local paths are passed through untouched, which
    /// makes refreshing a game's metadata idempotent.
    /// </summary>
    private async Task<string?> RehostImageAsync(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || remoteUrl.StartsWith("/media/"))
            return remoteUrl;

        // The data directory, NOT wwwroot. Re-hosted art used to be written inside the published
        // application, where an in-place upgrade deletes it along with the rest of the app folder —
        // silently emptying the library's covers. The stored "/media/..." URLs are unchanged; only
        // the physical location moved, and Program.cs maps that request path onto this directory.
        var mediaRoot = _settings.MediaDirectory;
        Directory.CreateDirectory(mediaRoot);

        try
        {
            var uri = new Uri(remoteUrl);
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrEmpty(extension)) extension = ".jpg";

            var fileName = $"{Guid.NewGuid()}{extension}";

            using var httpClient = new HttpClient();
            var imageBytes = await httpClient.GetByteArrayAsync(remoteUrl);
            await File.WriteAllBytesAsync(Path.Combine(mediaRoot, fileName), imageBytes);

            return $"/media/{fileName}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download image {remoteUrl}: {ex.Message}");
            return null;
        }
    }
}
