using System.Net.Http.Json;

namespace GameTownApp.Services;

/// <summary>
/// Client for the GameTown game endpoints and the RAWG metadata proxy.
///
/// Everything goes through System.Net.Http.Json, which uses JsonSerializerDefaults.Web
/// (camelCase, case-insensitive) and therefore lines up with ASP.NET Core's output without any
/// [JsonPropertyName] plumbing.
/// </summary>
public class GamesService(HttpClient http)
{
    private readonly HttpClient _http = http;

    // ---------------------------------------------------------------- RAWG metadata (proxied)

    public async Task<List<RawgGameContract>> SearchGamesMetadata(string query, int page = 1, int pageSize = 20)
    {
        var url = $"/meta/searchMetadata?query={Uri.EscapeDataString(query)}&page={page}&pageSize={pageSize}";
        return await _http.GetFromJsonAsync<List<RawgGameContract>>(url) ?? [];
    }

    public async Task<RawgGameContract?> GetRawgGameById(string rawgGameId)
        => await _http.GetFromJsonAsync<RawgGameContract>($"/meta/getGame/{Uri.EscapeDataString(rawgGameId)}");

    // ---------------------------------------------------------------- GameTown library (public)

    public async Task<List<GameContract>> GetPaged(
        int page = 1, int pageSize = 24, IEnumerable<string>? tags = null)
        => await _http.GetFromJsonAsync<List<GameContract>>(
               $"/GTGames/getPaged/{page}/{pageSize}{TagQuery(tags, first: true)}") ?? [];

    /// <summary>All three query parameters are required; omitting page/pageSize returns 400.</summary>
    public async Task<List<GameContract>> Search(
        string query, int page = 1, int pageSize = 24, IEnumerable<string>? tags = null)
    {
        var url = $"/GTGames/search/?query={Uri.EscapeDataString(query)}&page={page}&pageSize={pageSize}"
                  + TagQuery(tags, first: false);
        return await _http.GetFromJsonAsync<List<GameContract>>(url) ?? [];
    }

    /// <summary>
    /// Renders selected tags as one comma-separated parameter, or nothing when there are none.
    ///
    /// One parameter rather than repeating "tags=" per slug, because the same string is what the
    /// library page puts in its own address bar — "/?tags=lan,co-op" is legible and pasteable, which
    /// is the point of keeping the filter in the URL at all.
    /// </summary>
    private static string TagQuery(IEnumerable<string>? tags, bool first)
    {
        var slugs = (tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        if (slugs.Length == 0) return string.Empty;

        return (first ? "?" : "&") + "tags=" + Uri.EscapeDataString(string.Join(',', slugs));
    }

    public async Task<GameContract?> GetById(Guid id)
    {
        var response = await _http.GetAsync($"/GTGames/{id}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GameContract>();
    }

    /// <summary>Absolute URL for the download endpoint, for use as an anchor href.</summary>
    public string GetDownloadUrl(Guid id) => $"{_http.BaseAddress}GTGames/download/{id}";

    /// <summary>
    /// Makes a stored media path absolute against the API.
    ///
    /// Covers and screenshots are stored as "/media/{guid}.jpg" and served by the API, but the app
    /// runs on a different origin — so a browser resolves that root-relative path against the app
    /// and gets a 404. Every image has to go through here.
    /// </summary>
    public string? ResolveMedia(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
            return null;

        // Already absolute (e.g. a RAWG CDN URL left in place because re-hosting failed).
        // Deliberately a scheme check and not Uri.TryCreate(UriKind.Absolute): on Linux that
        // treats a leading-slash path as a valid absolute file:// URI, so "/media/x.jpg" would
        // come back as "file:///media/x.jpg".
        if (storedPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || storedPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return storedPath;
        }

        return _http.BaseAddress is null ? storedPath : new Uri(_http.BaseAddress, storedPath).ToString();
    }

    /// <summary>
    /// The image a game is shelved under: its box art, then RAWG's background image, then the first
    /// screenshot, then null — at which point the caller draws the title's initials.
    ///
    /// One implementation rather than one per page. The shelf and the detail page each had their own
    /// copy of this ladder, which is two places to add the box-art step to and two places for it to be
    /// added differently.
    /// </summary>
    public string? CoverFor(GameContract game)
    {
        // The override wins outright. Someone chose it precisely because the automatic answer below
        // was wrong, so falling back past it would undo their decision.
        if (!string.IsNullOrWhiteSpace(game.BoxArtUrl))
            return ResolveMedia(game.BoxArtUrl);

        var rawg = game.RawgGame;
        if (rawg is null) return null;

        // The screenshot step matters because RAWG does not always supply a background image.
        var path = !string.IsNullOrWhiteSpace(rawg.BackgroundImage)
            ? rawg.BackgroundImage
            : rawg.Screenshots.FirstOrDefault(s => !s.IsDeleted && !string.IsNullOrWhiteSpace(s.Image))?.Image;

        return ResolveMedia(path);
    }

    /// <summary>
    /// Up to two initials for a title, for the tile drawn when there is no art at all.
    ///
    /// Here rather than duplicated in each page for the same reason as <see cref="CoverFor"/>: it is
    /// the other half of the same fallback.
    /// </summary>
    public static string InitialsFor(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";

        var letters = words.Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0]));
        var result = string.Concat(letters);
        return string.IsNullOrEmpty(result) ? "?" : result;
    }

    // ---------------------------------------------------------------- contributor actions

    /// <summary>
    /// Absolute URL of the upload endpoint. Uploading goes through UploadService/upload.js rather
    /// than HttpClient, because fetch() cannot report upload progress — so the URL is handed out
    /// here instead of the request being made here.
    /// </summary>
    public string GetAddGameUrl() => $"{_http.BaseAddress}GTGames/Add";

    /// <summary>
    /// What the server will accept, so the upload form can refuse a file before sending it rather
    /// than after. Null if it could not be fetched — callers fall back to permissive defaults and
    /// let the server be the one to say no, which it does regardless.
    /// </summary>
    public async Task<UploadLimitsContract?> GetUploadLimits()
    {
        var response = await _http.GetAsync("/GTGames/upload-limits");
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<UploadLimitsContract>()
            : null;
    }

    public async Task<ApiResult> UpdateGame(GameTownGamePatchRequest request)
        => await ApiResult.FromResponse(await _http.PatchAsJsonAsync("/GTGames/update", request));

    public async Task<ApiResult> DeleteGame(Guid id)
        => await ApiResult.FromResponse(await _http.DeleteAsync($"/GTGames/{id}"));

    // ---------------------------------------------------------------- tags

    /// <summary>
    /// Every tag, with game counts. Anonymous, because the library's filter bar is anonymous.
    /// </summary>
    public async Task<List<TagContract>> GetTags(bool quickAddOnly = false)
        => await _http.GetFromJsonAsync<List<TagContract>>($"/tags/?quick={quickAddOnly}") ?? [];

    /// <summary>Replaces a game's whole tag set and returns what it now carries.</summary>
    public async Task<(ApiResult Result, List<TagContract> Tags)> SetGameTags(Guid id, IEnumerable<string> names)
    {
        var response = await _http.PutAsJsonAsync($"/tags/game/{id}",
            new SetGameTagsRequest { Names = [.. names] });

        var result = await ApiResult.FromResponse(response);
        if (!result.Success) return (result, []);

        return (result, await response.Content.ReadFromJsonAsync<List<TagContract>>() ?? []);
    }

    // ---------------------------------------------------------------- archive guide

    /// <summary>
    /// Writes the game's instructions into its archive as GameTownGuide.txt, or takes them out.
    ///
    /// Answers with the updated game so the caller refreshes from the response rather than issuing a
    /// second GET it would then have to keep in step.
    /// </summary>
    public async Task<(ApiResult Result, GameContract? Game)> SetArchiveGuide(Guid id, bool baked)
    {
        var response = await _http.PutAsJsonAsync($"/guide/{id}", new SetGuideRequest { Baked = baked });
        return await ReadGameResult(response);
    }

    // ---------------------------------------------------------------- box art

    /// <summary>
    /// Candidate cover art for a title.
    ///
    /// Always answers 200, carrying a reason code — "not-configured" and "unreachable" are states the
    /// picker explains rather than request failures, and an error status would collapse them into the
    /// generic failure branch here.
    /// </summary>
    public async Task<BoxArtSearchResult> SearchBoxArt(string title)
    {
        var url = $"/boxart/search?title={Uri.EscapeDataString(title)}";
        var response = await _http.GetAsync(url);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<BoxArtSearchResult>() ?? new()
            : new BoxArtSearchResult { Reason = "unreachable" };
    }

    /// <summary>Has the server download an image and adopt it as the game's box art.</summary>
    public async Task<(ApiResult Result, GameContract? Game)> SetBoxArtFromUrl(Guid id, string url)
    {
        var response = await _http.PostAsJsonAsync($"/boxart/{id}", new SetBoxArtRequest { Url = url });
        return await ReadGameResult(response);
    }

    /// <summary>
    /// Uploads an image file as the game's box art.
    ///
    /// Plain HttpClient rather than the XHR path the archive upload uses: cover art is a few hundred
    /// kilobytes, so there is no progress worth reporting and nothing that would strain the WASM heap.
    /// </summary>
    public async Task<(ApiResult Result, GameContract? Game)> UploadBoxArt(
        Guid id, Stream content, string fileName)
    {
        using var form = new MultipartFormDataContent();
        using var file = new StreamContent(content);
        form.Add(file, "file", fileName);

        var response = await _http.PostAsync($"/boxart/{id}/upload", form);
        return await ReadGameResult(response);
    }

    /// <summary>Drops the override so the game falls back to its RAWG image.</summary>
    public async Task<ApiResult> ClearBoxArt(Guid id)
        => await ApiResult.FromResponse(await _http.DeleteAsync($"/boxart/{id}"));

    /// <summary>
    /// Both box-art writes answer with the updated game, so the caller can refresh from the response
    /// instead of issuing a second GET it would then have to keep in step.
    /// </summary>
    private static async Task<(ApiResult, GameContract?)> ReadGameResult(HttpResponseMessage response)
    {
        var result = await ApiResult.FromResponse(response);
        if (!result.Success) return (result, null);

        return (result, await response.Content.ReadFromJsonAsync<GameContract>());
    }
}
