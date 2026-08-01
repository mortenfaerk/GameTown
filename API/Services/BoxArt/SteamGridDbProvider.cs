using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace API.Services.BoxArt;

/// <summary>
/// Box art from SteamGridDB.
///
/// Chosen over a general image search because it is the only source that already knows what "box art"
/// means for a game: its "grids" are curated 600x900 portrait covers, which is exactly the shape the
/// shelf renders. A web image search returns screenshots, fan art and thumbnails of review pages, and
/// a human then has to sort them — which is the work this feature is supposed to remove.
///
/// Two calls per search. SteamGridDB keys artwork off its own game id, so the title has to be
/// resolved first (<c>search/autocomplete</c>) and the grids fetched for the best match.
///
/// The key is read from settings on every call, never captured in the constructor. That is the
/// documented failure mode in this codebase: RAWGService and FileService both used to take their
/// configuration as constructor arguments resolved once at startup, which is what made the settings
/// page appear to save and change nothing.
/// </summary>
public class SteamGridDbProvider(SettingsService settings, IHttpClientFactory httpClientFactory)
    : IBoxArtProvider
{
    public const string HttpClientName = "steamgriddb";
    private const string BaseAddress = "https://www.steamgriddb.com/api/v2";

    /// <summary>How many candidates to offer. A picker grid, not a catalogue.</summary>
    private const int MaxCandidates = 24;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "SteamGridDB";

    public async Task<BoxArtSearchResult> SearchAsync(string title, CancellationToken cancellationToken = default)
    {
        var key = await settings.GetBoxArtApiKeyAsync();
        if (key is null)
            return new BoxArtSearchResult { Reason = "not-configured" };

        if (string.IsNullOrWhiteSpace(title))
            return new BoxArtSearchResult { Reason = "no-match" };

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            client.DefaultRequestHeaders.Authorization = new("Bearer", key);

            var gameId = await ResolveGameIdAsync(client, title, cancellationToken);
            if (gameId is null)
                return new BoxArtSearchResult { Reason = "no-match" };

            // 600x900 static grids only: that is the portrait cover format. Animated grids are
            // excluded because the shelf renders an <img> and would show a still frame anyway, and
            // NSFW/humour artwork is excluded because this is a shared family-visible library.
            var url = $"{BaseAddress}/grids/game/{gameId}"
                      + "?dimensions=600x900&types=static&nsfw=false&humor=false";

            using var response = await client.GetAsync(url, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new BoxArtSearchResult { Reason = "rejected" };

            if (!response.IsSuccessStatusCode)
                return new BoxArtSearchResult { Reason = "unreachable" };

            var payload = await response.Content.ReadFromJsonAsync<GridsResponse>(Json, cancellationToken);
            var grids = payload?.Data ?? [];

            var candidates = grids
                .Where(g => !string.IsNullOrWhiteSpace(g.Url))
                .Take(MaxCandidates)
                .Select(g => new BoxArtCandidateContract
                {
                    FullUrl = g.Url!,
                    // The thumb is what the picker grid loads, two dozen at a time. Falling back to
                    // the full image keeps a provider that omits it usable, just heavier.
                    ThumbUrl = string.IsNullOrWhiteSpace(g.Thumb) ? g.Url! : g.Thumb!,
                    Width = g.Width,
                    Height = g.Height,
                    Source = Name,
                })
                .ToList();

            return new BoxArtSearchResult
            {
                Candidates = candidates,
                Reason = candidates.Count == 0 ? "no-match" : "ok",
            };
        }
        catch (Exception)
        {
            // No exception detail to the caller — see SettingsEndpoints.TestRawgKey for the reasoning.
            return new BoxArtSearchResult { Reason = "unreachable" };
        }
    }

    /// <summary>
    /// The provider's own id for a title, or null if it does not recognise it.
    ///
    /// Takes the first result rather than trying to be clever about matching. The autocomplete
    /// endpoint is already ranked by relevance, and when it is wrong the contributor can see that
    /// immediately in the thumbnails and search again with a different spelling — which is a better
    /// remedy than a scoring heuristic that is wrong less visibly.
    /// </summary>
    private static async Task<int?> ResolveGameIdAsync(
        HttpClient client, string title, CancellationToken cancellationToken)
    {
        var url = $"{BaseAddress}/search/autocomplete/{Uri.EscapeDataString(title.Trim())}";
        using var response = await client.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode) return null;

        var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(Json, cancellationToken);
        return payload?.Data?.FirstOrDefault()?.Id;
    }

    // Only the fields actually used are declared. SteamGridDB returns a good deal more, and binding
    // all of it would make an upstream addition a compilation concern for no gain.
    private sealed record SearchResponse([property: JsonPropertyName("data")] List<SearchGame>? Data);
    private sealed record SearchGame([property: JsonPropertyName("id")] int Id);

    private sealed record GridsResponse([property: JsonPropertyName("data")] List<Grid>? Data);
    private sealed record Grid(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("thumb")] string? Thumb,
        [property: JsonPropertyName("width")] int? Width,
        [property: JsonPropertyName("height")] int? Height);
}
