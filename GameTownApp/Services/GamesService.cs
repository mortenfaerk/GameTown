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

    public async Task<List<GameContract>> GetPaged(int page = 1, int pageSize = 24)
        => await _http.GetFromJsonAsync<List<GameContract>>($"/GTGames/getPaged/{page}/{pageSize}") ?? [];

    /// <summary>All three query parameters are required; omitting page/pageSize returns 400.</summary>
    public async Task<List<GameContract>> Search(string query, int page = 1, int pageSize = 24)
    {
        var url = $"/GTGames/search/?query={Uri.EscapeDataString(query)}&page={page}&pageSize={pageSize}";
        return await _http.GetFromJsonAsync<List<GameContract>>(url) ?? [];
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
}
