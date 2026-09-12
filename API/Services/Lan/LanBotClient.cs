using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace API.Services.Lan;

/// <summary>
/// The LAN Discord bot's Catalogue API — the only place GameTown talks to it.
///
/// HOW THE BOT'S MODEL WORKS, because nothing else in this file makes sense without it: the bot keeps
/// a catalogue keyed by a GUID the CLIENT chooses. A game push (<see cref="UpsertGameAsync"/>) writes
/// that entry's title/url/boxArtUrl and, as a side effect, adopts any currently-unmatched suggestion
/// whose name case-insensitively equals the pushed title — but a direct bind
/// (<see cref="MatchSuggestionAsync"/>) is the reliable way to link a specific suggestion, since it
/// works regardless of what the suggestion's raw text looks like and regardless of whether it is
/// already bound to something else.
///
/// Two consequences worth knowing before changing anything here:
///
///  * <b>A game push never overwrites an existing match.</b> Only <see cref="MatchSuggestionAsync"/>
///    can re-point a suggestion that is already bound — it does so unconditionally, last-writer-wins.
///  * <b>The title we send is GameTown's canonical one.</b> Binding no longer depends on it matching
///    a suggestion's raw text, so there is no reason to send anything else.
///
/// Settings are read on EVERY call and never captured in the constructor. That is the documented
/// failure mode in this codebase — the old RAWGService and FileService took their configuration as
/// constructor arguments resolved once at startup, which is what made the settings page appear to
/// save and change nothing. It matters more here than anywhere: this runs on a timer, so a stale
/// value would produce a service quietly calling the wrong host with nobody watching.
/// </summary>
public class LanBotClient(
    SettingsService settings,
    IHttpClientFactory httpClientFactory,
    ILogger<LanBotClient> logger)
{
    /// <summary>
    /// Registered WITHOUT <c>ImageFetcher.BuildHandler</c>, and that is deliberate — see
    /// SECURITY-NOTES.md risk 10. That handler refuses to connect to private addresses, which is
    /// right for a URL a contributor pasted and wrong for this one: the bot may well live on the same
    /// LAN as the appliance, and this address is chosen by an administrator, not by a visitor.
    /// </summary>
    public const string HttpClientName = "lanbot";

    /// <summary>
    /// Page size for the suggestions pull. Comfortably larger than any real LAN's list, so the common
    /// case is one request, while the loop still exists for the day it is not.
    /// </summary>
    private const int PageSize = 100;

    /// <summary>
    /// A ceiling on pages, so a bot reporting a nonsense <c>total</c> cannot spin this forever. Picked
    /// to be absurd rather than tight — hitting it means the far end is broken, not that a LAN got big.
    /// </summary>
    private const int MaxPages = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Whether both halves of the credential are stored. Cheap; no outbound call.</summary>
    public async Task<bool> IsConfiguredAsync()
    {
        var (baseUrl, apiKey) = await settings.GetLanBotCredentialsAsync();
        return baseUrl is not null && apiKey is not null;
    }

    /// <summary>
    /// Every suggestion the bot knows about, paged until <c>total</c> is reached.
    ///
    /// Returns the reason code alongside a possibly-partial list, and the caller must check it: an
    /// empty list with reason "ok" means the bot has nothing, while an empty list with reason
    /// "unreachable" means we learned nothing at all. Conflating those would let a failed poll look
    /// like every suggestion having been withdrawn, and reconciliation would then unbind the lot.
    /// </summary>
    public async Task<(List<LanBotSuggestion> Items, string Reason)> GetSuggestionsAsync(
        CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync();
        if (client is null) return ([], "not-configured");

        using var _ = client;
        var collected = new List<LanBotSuggestion>();

        try
        {
            for (var page = 0; page < MaxPages; page++)
            {
                var offset = page * PageSize;

                using var response = await client.GetAsync(
                    $"api/v1/suggestions?limit={PageSize}&offset={offset}", cancellationToken);

                var failure = Classify(response);
                if (failure is not null) return (collected, failure);

                var payload = await response.Content.ReadFromJsonAsync<SuggestionsPage>(Json, cancellationToken);
                var items = payload?.Items ?? [];

                collected.AddRange(items);

                // A short page ends the walk even when total disagrees; total is the far end's claim
                // about itself and the items are the thing we actually received.
                if (items.Count < PageSize || collected.Count >= (payload?.Total ?? 0)) break;
            }

            return (collected, "ok");
        }
        catch (Exception exception)
        {
            return (collected, Unreachable(exception, "reading suggestions"));
        }
    }

    /// <summary>
    /// Creates or updates the catalogue entry for a game — title, deep link and cover art.
    ///
    /// <paramref name="url"/> and <paramref name="boxArtUrl"/> are REPLACED WHOLESALE on every call —
    /// omitting either one clears it at the bot, since that is the only way to retract a link pushed
    /// earlier. Both are serialised with <c>JsonIgnore(WhenWritingNull)</c> so passing null actually
    /// omits the key rather than sending an explicit null. As a side effect the bot adopts any
    /// currently-unmatched suggestion whose name case-insensitively equals <paramref name="title"/>;
    /// an existing match is never overwritten by this call — see <see cref="MatchSuggestionAsync"/>
    /// for that. Returns the number of suggestions the bot says it bound this way — informational; the
    /// authority on what is bound is the next sync's <c>gameTownMatchId</c>, not this counter.
    /// </summary>
    public async Task<(bool Ok, string Reason, int Bound)> UpsertGameAsync(
        Guid matchId, string title, string? url, string? boxArtUrl,
        CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync();
        if (client is null) return (false, "not-configured", 0);

        using var _ = client;

        try
        {
            using var response = await client.PutAsJsonAsync(
                $"api/v1/games/{matchId}", new UpsertGameRequest(title, url, boxArtUrl), Json,
                cancellationToken);

            var failure = Classify(response);
            if (failure is not null) return (false, failure, 0);

            var payload = await response.Content.ReadFromJsonAsync<UpsertGameResponse>(Json, cancellationToken);
            return (true, "ok", payload?.SuggestionsBound ?? 0);
        }
        catch (Exception exception)
        {
            return (false, Unreachable(exception, "upserting a catalogue entry"), 0);
        }
    }

    /// <summary>
    /// Binds one suggestion to a catalogue entry directly, regardless of whatever it is currently
    /// bound to.
    ///
    /// Unconditional and unlike a game push's name-based adoption: this re-points an already-bound
    /// suggestion in one call, last-writer-wins, no delete-then-bind. The catalogue entry must already
    /// exist — <see cref="UpsertGameAsync"/> first, or this answers "game-not-found" (the bot's 400).
    /// </summary>
    public async Task<(bool Ok, string Reason)> MatchSuggestionAsync(
        long remoteId, Guid matchId, CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync();
        if (client is null) return (false, "not-configured");

        using var _ = client;

        try
        {
            using var response = await client.PutAsJsonAsync(
                $"api/v1/suggestions/{remoteId}/match", new MatchSuggestionRequest(matchId), Json,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.BadRequest) return (false, "game-not-found");
            if (response.StatusCode == HttpStatusCode.NotFound) return (false, "not-found");

            var failure = Classify(response);
            return failure is null ? (true, "ok") : (false, failure);
        }
        catch (Exception exception)
        {
            return (false, Unreachable(exception, "binding a suggestion"));
        }
    }

    /// <summary>
    /// Clears one suggestion's match, leaving the catalogue entry and every other suggestion bound to
    /// it untouched.
    ///
    /// 204 whether or not a match was there to clear — idempotent, the same way
    /// <c>SteamGridDbProvider</c> and friends treat a 404 as "already gone" rather than a failure.
    /// </summary>
    public async Task<(bool Ok, string Reason)> UnmatchSuggestionAsync(
        long remoteId, CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync();
        if (client is null) return (false, "not-configured");

        using var _ = client;

        try
        {
            using var response = await client.DeleteAsync(
                $"api/v1/suggestions/{remoteId}/match", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound) return (false, "not-found");

            var failure = Classify(response);
            return failure is null ? (true, "ok") : (false, failure);
        }
        catch (Exception exception)
        {
            return (false, Unreachable(exception, "clearing a suggestion's match"));
        }
    }

    /// <summary>
    /// One live call with the stored credentials, to tell "saved" from "works" — the same job
    /// <c>SettingsEndpoints.TestIgdbCredentials</c> does for the metadata provider.
    ///
    /// Deliberately a real authenticated request rather than <c>/health</c>: health is unauthenticated
    /// and would report "ok" for a wrong API key, which is the single most likely thing to be wrong.
    /// </summary>
    public async Task<string> PingAsync(CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync();
        if (client is null) return "not-configured";

        using var _ = client;

        try
        {
            using var response = await client.GetAsync("api/v1/suggestions?limit=1", cancellationToken);
            return Classify(response) ?? "ok";
        }
        catch (Exception exception)
        {
            return Unreachable(exception, "testing the connection");
        }
    }

    /// <summary>
    /// A configured client, or null when the credential pair is incomplete.
    ///
    /// Built per call, because the base address and key come from settings that may have changed since
    /// the last one. The handler underneath is still pooled by IHttpClientFactory, so this costs an
    /// object rather than a connection.
    /// </summary>
    private async Task<HttpClient?> CreateClientAsync()
    {
        var (baseUrl, apiKey) = await settings.GetLanBotCredentialsAsync();
        if (baseUrl is null || apiKey is null) return null;

        if (!Uri.TryCreate(EnsureTrailingSlash(baseUrl), UriKind.Absolute, out var uri)) return null;

        var client = httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = uri;
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        return client;
    }

    /// <summary>
    /// The base address must end in a slash or <see cref="Uri"/> drops the last path segment when it
    /// resolves a relative request — a bot hosted under a sub-path would silently lose it.
    /// </summary>
    private static string EnsureTrailingSlash(string baseUrl)
        => baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";

    /// <summary>
    /// Maps a response to a reason code, or null when it succeeded.
    ///
    /// The vocabulary is the project's fixed one, shared with the metadata and artwork providers so
    /// the UI can translate all three with one switch. Never exception text and never a response body:
    /// this reports on a request to an address an administrator configured, which may be internal.
    /// </summary>
    private string? Classify(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return null;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return "rejected";

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return "rate-limited";

        logger.LogWarning("The LAN bot answered with status {Status}.", (int)response.StatusCode);
        return "unreachable";
    }

    /// <summary>Logs the exception TYPE only — its message can carry the configured host.</summary>
    private string Unreachable(Exception exception, string action)
    {
        logger.LogWarning("The LAN bot could not be reached while {Action} ({Type}).",
            action, exception.GetType().Name);

        return "unreachable";
    }

    // Only the fields GameTown uses are bound. The bot's GameDto also carries source/createdAtUtc/
    // updatedAtUtc, none of which changes a decision here.

    private sealed record SuggestionsPage(int Total, List<LanBotSuggestion>? Items);

    private sealed record UpsertGameRequest(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Url,
        [property: JsonPropertyName("boxArtUrl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? BoxArtUrl);

    private sealed record UpsertGameResponse(bool Created, int SuggestionsBound);

    private sealed record MatchSuggestionRequest([property: JsonPropertyName("matchId")] Guid MatchId);
}

/// <summary>
/// One suggestion as the bot reports it.
///
/// <see cref="GameTownMatchId"/> is the authority on what the bot currently has bound, and sync
/// reconciles local state to it rather than the other way round — see
/// <c>LanSuggestionService.SyncAsync</c>.
/// </summary>
public sealed record LanBotSuggestion(
    long Id,
    string Name,
    string LanEventName,
    Guid? GameTownMatchId,
    bool Played);
