using System.Net.Http.Json;

namespace GameTownApp.Services;

/// <summary>
/// Client for the admin re-link endpoints.
///
/// Named the same as the API-side service on purpose — they are two halves of one feature and the
/// namespaces keep them apart.
/// </summary>
public class MetadataRelinkService(HttpClient http)
{
    private readonly HttpClient _http = http;

    public async Task<List<RelinkCandidateContract>> GetCandidates()
        => await _http.GetFromJsonAsync<List<RelinkCandidateContract>>("/metadata-relink/candidates") ?? [];

    /// <summary>
    /// Asks the server to search for a match per game. Writes nothing — the response is a set of
    /// proposals for an administrator to confirm.
    ///
    /// Can take a while for a large library: the server paces its searches to stay inside the
    /// provider's rate limit, so this is deliberately one call the UI shows as busy rather than a
    /// stream of small ones.
    /// </summary>
    public async Task<List<RelinkProposalContract>> Propose(List<Guid> gameIds)
    {
        var response = await _http.PostAsJsonAsync(
            "/metadata-relink/propose", new RelinkProposeRequest { GameIds = gameIds });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<RelinkProposalContract>>() ?? [];
    }

    public async Task<RelinkApplyResult> Apply(List<RelinkPairing> pairings)
    {
        var response = await _http.PostAsJsonAsync(
            "/metadata-relink/apply", new RelinkApplyRequest { Pairings = pairings });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RelinkApplyResult>() ?? new RelinkApplyResult();
    }
}
