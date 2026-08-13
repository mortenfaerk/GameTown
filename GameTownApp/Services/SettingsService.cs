using System.Net.Http.Json;

namespace GameTownApp.Services;

/// <summary>
/// Client for the /settings endpoints. Every one requires the Admin policy.
///
/// The update call returns the new settings state, so callers should adopt what comes back rather
/// than assuming what they sent took effect — the server normalises file-type lists and may reject
/// an unusable path.
/// </summary>
public class SettingsService(HttpClient http)
{
    private readonly HttpClient _http = http;

    public async Task<SettingsContract?> GetSettings()
    {
        var response = await _http.GetAsync("/settings");
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<SettingsContract>();
    }

    /// <summary>
    /// Applies a partial change. On success the second item is the server's new state.
    /// </summary>
    public async Task<(ApiResult Result, SettingsContract? Settings)> UpdateSettings(SettingsUpdateRequest request)
    {
        var response = await _http.PatchAsJsonAsync("/settings", request);
        var result = await ApiResult.FromResponse(response);
        if (!result.Success)
            return (result, null);

        return (result, await response.Content.ReadFromJsonAsync<SettingsContract>());
    }

    public async Task<PathCheckResult?> CheckPath(string path)
    {
        var response = await _http.PostAsJsonAsync("/settings/check-path", new PathCheckRequest { Path = path });
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<PathCheckResult>();
    }

    /// <summary>
    /// One live IGDB call with the stored credentials — the only way to tell "saved" from "works".
    ///
    /// Worth more here than the RAWG key check it replaces: authenticating with IGDB is two calls
    /// against two hosts, and credentials copied out of the Twitch console are easy to transpose.
    /// </summary>
    public async Task<ProviderCredentialCheckResult?> TestIgdbCredentials()
    {
        var response = await _http.PostAsync("/settings/test-igdb-credentials", null);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<ProviderCredentialCheckResult>();
    }

    /// <summary>
    /// One live artwork-provider call with the stored key. Shares the result shape: "did it work, and
    /// if not why" is the same question for both providers.
    /// </summary>
    public async Task<ProviderCredentialCheckResult?> TestBoxArtKey()
    {
        var response = await _http.PostAsync("/settings/test-boxart-key", null);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<ProviderCredentialCheckResult>();
    }
}
