using System.Net.Http.Json;

namespace GameTownApp.Services;

/// <summary>
/// Client for the LAN bot suggestion endpoints.
///
/// Named the same as the API-side service on purpose — they are two halves of one feature and the
/// namespaces keep them apart, exactly as with <see cref="MetadataRelinkService"/>.
/// </summary>
public class LanSuggestionService(HttpClient http)
{
    private readonly HttpClient _http = http;

    /// <param name="state">"unmatched", "matched", "dismissed" or "all".</param>
    public async Task<List<LanSuggestionContract>> Get(string state)
        => await _http.GetFromJsonAsync<List<LanSuggestionContract>>($"/lan/suggestions?state={state}") ?? [];

    public async Task<List<LanCandidateContract>> GetCandidates(long remoteId)
        => await _http.GetFromJsonAsync<List<LanCandidateContract>>(
               $"/lan/suggestions/{remoteId}/candidates") ?? [];

    /// <summary>
    /// How many suggestions are waiting on a human — the same question as the unmatched tab, for the
    /// sidebar, which asks on every app load and must not pull the whole list to learn one number.
    /// </summary>
    public async Task<int> GetUnmatchedCount()
        => (await _http.GetFromJsonAsync<LanCountContract>("/lan/count"))?.Unmatched ?? 0;

    /// <summary>
    /// Links a suggestion to a library entry.
    ///
    /// Linking a game other suggestions already point at needs no confirmation: bindings at the bot
    /// are additive, so the existing ones keep working.
    /// </summary>
    public async Task<LanLinkResult> Link(long remoteId, Guid gameId)
    {
        var response = await _http.PostAsJsonAsync("/lan/link",
            new LanLinkRequest { RemoteId = remoteId, GameId = gameId });

        response.EnsureSuccessStatusCode();
        return await Read(response);
    }

    public async Task<LanLinkResult> Unlink(long remoteId)
    {
        var response = await _http.PostAsJsonAsync("/lan/unlink", new LanRemoteIdRequest { RemoteId = remoteId });
        response.EnsureSuccessStatusCode();
        return await Read(response);
    }

    public async Task<LanLinkResult> Dismiss(long remoteId, bool dismissed)
    {
        var response = await _http.PostAsJsonAsync("/lan/dismiss",
            new LanDismissRequest { RemoteId = remoteId, Dismissed = dismissed });

        response.EnsureSuccessStatusCode();
        return await Read(response);
    }

    /// <summary>
    /// LAN events with at least one library game, for the shelf chips.
    ///
    /// Anonymous, unlike everything else here — the shelf is public, so this is asked on the library
    /// page by visitors with no account. A failure is not worth surfacing: the chips are an offer, and
    /// an empty list simply means the row does not render.
    /// </summary>
    public async Task<List<LanEventContract>> GetEvents()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<LanEventContract>>("/lan/events") ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Admin only. The settings screen's status panel.</summary>
    public async Task<LanSyncStatusContract?> GetStatus()
        => await _http.GetFromJsonAsync<LanSyncStatusContract>("/lan/status");

    /// <summary>
    /// Runs a sync now and returns the resulting status, so the screen needs no second request.
    ///
    /// Admin only, and slow enough to want a busy state: it pulls every suggestion and may make one
    /// outbound write per newly matched game.
    /// </summary>
    public async Task<LanSyncStatusContract?> SyncNow()
    {
        var response = await _http.PostAsync("/lan/sync", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LanSyncStatusContract>();
    }

    private static async Task<LanLinkResult> Read(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<LanLinkResult>()
           ?? new LanLinkResult { Ok = false, Reason = "unreachable" };
}
