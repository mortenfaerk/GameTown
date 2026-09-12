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
    /// The wishlist, ranked against the library and banded, in one request.
    ///
    /// This is what the screen actually loads. Ranking per row on demand meant a person had to open
    /// every row to discover which kind of row it was — the fourteen with an obvious answer looked
    /// exactly like the thirty-two with none.
    /// </summary>
    public async Task<LanRankedQueueContract> GetRanked(
        string? query = null, string? lanEvent = null, string? confidence = null,
        int page = 1, int pageSize = 50)
    {
        var url = $"/lan/suggestions/ranked?page={page}&pageSize={pageSize}";

        if (!string.IsNullOrWhiteSpace(query)) url += $"&q={Uri.EscapeDataString(query)}";
        // Escaped rather than passed through: event names come from Discord and routinely carry a "#"
        // ("HCP #37 (2026)"), which would otherwise truncate the query string at the fragment.
        if (!string.IsNullOrWhiteSpace(lanEvent)) url += $"&lan={Uri.EscapeDataString(lanEvent)}";
        if (!string.IsNullOrWhiteSpace(confidence)) url += $"&confidence={confidence}";

        return await _http.GetFromJsonAsync<LanRankedQueueContract>(url) ?? new LanRankedQueueContract();
    }

    /// <summary>
    /// Both counts in one request, for the sidebar link — which asks on every app load and used to
    /// pull the whole suggestion list to learn the second one.
    /// </summary>
    public async Task<LanCountContract> GetCounts()
        => await _http.GetFromJsonAsync<LanCountContract>("/lan/count") ?? new LanCountContract();

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

    /// <summary>
    /// Links several suggestions in one request.
    ///
    /// Sequenced on the SERVER, not here: these are outbound calls to a rate-limited bot, and a loop
    /// in the browser puts the pacing in a page that can be closed halfway through. The result is per
    /// row because a batch can partially fail — some rows link, others don't.
    /// </summary>
    public async Task<LanBulkResult> LinkMany(IEnumerable<LanLinkRequest> links)
    {
        var response = await _http.PostAsJsonAsync("/lan/link-many",
            new LanBulkLinkRequest { Links = [.. links] });

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LanBulkResult>() ?? new LanBulkResult();
    }

    public async Task<LanBulkResult> DismissMany(IEnumerable<long> remoteIds, bool dismissed)
    {
        var response = await _http.PostAsJsonAsync("/lan/dismiss-many",
            new LanBulkDismissRequest { RemoteIds = [.. remoteIds], Dismissed = dismissed });

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LanBulkResult>() ?? new LanBulkResult();
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
    /// Open to contributors — it is how the wishlist gets refreshed without waiting out the poll
    /// interval — and slow enough to want a busy state: it pulls every suggestion and may make one
    /// outbound write per newly matched game. A reason of "busy" means a sync was already running and
    /// this call did nothing.
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
