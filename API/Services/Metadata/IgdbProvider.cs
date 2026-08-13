using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

namespace API.Services.Metadata;

/// <summary>
/// IGDB, reached through Twitch-issued credentials. The metadata source that replaced RAWG.
///
/// Two things differ from the RAWG client this succeeds, beyond the obvious:
///
/// <b>One request per game, not one plus pagination.</b> RAWG served screenshots from a separate
/// paginated endpoint, so <c>EnsureRawgGamePersisted</c> carried a loop that fetched pages until the
/// count matched — with a null-guard in it because an error body part-way through used to take the
/// whole request down. Apicalypse expands related records inline (<c>screenshots.image_id</c>), so a
/// full game is one POST and that loop has no successor.
///
/// <b>Images are addressed by id, not by URL.</b> IGDB returns an <c>image_id</c> and the URL is built
/// from a documented template with a size token. That means the host is fixed and the shape is known —
/// and it changes nothing about the rules: every download still goes through <c>ImageFetcher</c>,
/// which sniffs magic bytes rather than trusting the extension. A provider that starts returning
/// something other than an image is exactly the case that check exists for, and "we build the URL
/// ourselves" is not a reason to trust the bytes at the end of it.
/// </summary>
public class IgdbProvider(
    SettingsService settings,
    IgdbTokenProvider tokens,
    IHttpClientFactory httpClientFactory,
    ILogger<IgdbProvider> logger) : IGameMetadataProvider
{
    public const string HttpClientName = "igdb-api";
    private const string GamesEndpoint = "https://api.igdb.com/v4/games";

    /// <summary>
    /// Built from IGDB's documented CDN template. <c>t_cover_big</c> is 264x374 — portrait, which is
    /// the shape a shelf wants and the thing RAWG never had. <c>t_720p</c> for screenshots.
    /// </summary>
    private const string ImageUrlTemplate = "https://images.igdb.com/igdb/image/upload/t_{0}/{1}.jpg";

    public string Id => "igdb";
    public string DisplayName => "IGDB";

    /// <summary>
    /// The fields every full fetch asks for. Kept as one constant so the query the search uses and the
    /// query the persister uses cannot drift into disagreeing about what a game record contains.
    /// </summary>
    private const string GameFields =
        "fields id, name, slug, summary, first_release_date, aggregated_rating, rating, url, " +
        "cover.image_id, screenshots.image_id, screenshots.width, screenshots.height, " +
        "genres.name, genres.slug, " +
        "involved_companies.company.id, involved_companies.company.name, " +
        "involved_companies.company.slug, involved_companies.developer;";

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var (clientId, clientSecret) = await settings.GetIgdbCredentialsAsync();
        return clientId is not null && clientSecret is not null;
    }

    public async Task<ProviderSearchResponse> SearchAsync(
        string query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return ProviderSearchResponse.Ok([]);

        // Bounded here as well as at the endpoint: this is the value that goes into "limit"/"offset",
        // and IGDB caps page size at 500 anyway.
        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(page, 1);
        var offset = (page - 1) * pageSize;

        // game_type = 0 is "main game". Without it the results are dominated by mods and episode
        // re-releases — searching "half-life" returned three Half-Life 2 MMod entries before
        // Half-Life itself. This is a relevance fix, verified against the live API, not a filter the
        // user asked for; a library catalogues games, not mods of games.
        var body = new StringBuilder()
            .Append("fields id, name, slug, first_release_date, cover.image_id; ")
            .Append($"search {ApicalypseQuery.Quote(query)}; ")
            .Append("where game_type = 0; ")
            .Append($"limit {pageSize}; offset {offset};")
            .ToString();

        var (json, reason) = await PostAsync(body, cancellationToken);
        if (json is null) return ProviderSearchResponse.Failed(reason);

        try
        {
            using var document = JsonDocument.Parse(json);
            var results = new List<ProviderSearchResult>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var id = GetInt(element, "id");
                var name = GetString(element, "name");
                if (id is null || string.IsNullOrEmpty(name)) continue;

                results.Add(new ProviderSearchResult
                {
                    Provider = Id,
                    ExternalId = id.Value,
                    Name = name,
                    Slug = GetString(element, "slug") ?? string.Empty,
                    Released = GetUnixDate(element, "first_release_date"),
                    ThumbnailUrl = element.TryGetProperty("cover", out var cover)
                        ? BuildImageUrl(GetString(cover, "image_id"), "cover_big")
                        : null
                });
            }

            return ProviderSearchResponse.Ok(results);
        }
        catch (JsonException)
        {
            logger.LogWarning("IGDB returned a search body that could not be parsed.");
            return ProviderSearchResponse.Failed("unreachable");
        }
    }

    public async Task<ProviderGame?> GetAsync(int externalId, CancellationToken cancellationToken = default)
    {
        var body = $"{GameFields} where id = {externalId}; limit 1;";

        var (json, _) = await PostAsync(body, cancellationToken);
        if (json is null) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var element = document.RootElement.EnumerateArray().FirstOrDefault();
            if (element.ValueKind != JsonValueKind.Object) return null;

            return MapGame(element);
        }
        catch (JsonException)
        {
            logger.LogWarning("IGDB returned a game body that could not be parsed.");
            return null;
        }
    }

    private ProviderGame? MapGame(JsonElement element)
    {
        var id = GetInt(element, "id");
        var name = GetString(element, "name");
        if (id is null || string.IsNullOrEmpty(name)) return null;

        var developers = new List<ProviderCompany>();
        if (element.TryGetProperty("involved_companies", out var companies)
            && companies.ValueKind == JsonValueKind.Array)
        {
            foreach (var involved in companies.EnumerateArray())
            {
                // Publishers come back in the same array and are not what the detail page means by
                // "developer". The flag is the only thing separating them.
                if (!involved.TryGetProperty("developer", out var isDeveloper)
                    || isDeveloper.ValueKind != JsonValueKind.True) continue;

                if (!involved.TryGetProperty("company", out var company)) continue;

                var companyId = GetInt(company, "id");
                if (companyId is null) continue;

                developers.Add(new ProviderCompany(
                    companyId.Value, GetString(company, "name"), GetString(company, "slug")));
            }
        }

        var genres = new List<ProviderGenre>();
        if (element.TryGetProperty("genres", out var genreArray) && genreArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var genre in genreArray.EnumerateArray())
            {
                var genreId = GetInt(genre, "id");
                if (genreId is null) continue;
                genres.Add(new ProviderGenre(genreId.Value, GetString(genre, "name"), GetString(genre, "slug")));
            }
        }

        var screenshots = new List<ProviderScreenshot>();
        if (element.TryGetProperty("screenshots", out var shotArray) && shotArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var shot in shotArray.EnumerateArray())
            {
                var shotId = GetInt(shot, "id");
                var url = BuildImageUrl(GetString(shot, "image_id"), "720p");
                if (shotId is null || url is null) continue;

                screenshots.Add(new ProviderScreenshot(
                    shotId.Value, url, GetInt(shot, "width") ?? 0, GetInt(shot, "height") ?? 0));
            }
        }

        return new ProviderGame
        {
            Provider = Id,
            ExternalId = id.Value,
            Name = name,
            Slug = GetString(element, "slug") ?? string.Empty,
            Description = ToHtml(GetString(element, "summary")),
            Released = GetUnixDate(element, "first_release_date"),
            CriticScore = GetDouble(element, "aggregated_rating"),
            Rating = GetDouble(element, "rating"),
            Website = GetString(element, "url") ?? string.Empty,
            ImageUrl = element.TryGetProperty("cover", out var cover)
                ? BuildImageUrl(GetString(cover, "image_id"), "cover_big")
                : null,
            Developers = developers,
            Genres = genres,
            Screenshots = screenshots
        };
    }

    /// <summary>
    /// Turns IGDB's plain-text summary into the HTML the description column holds.
    ///
    /// The column is not uniform by accident — after migration 007 it contains RAWG's HTML on carried
    /// rows and IGDB's text on new ones, and the client renders all of it through <c>MarkupString</c>,
    /// which does no encoding. Storing plain text raw would mean a summary containing "&lt;3" loses
    /// three characters, and one containing anything sharper does worse than that.
    ///
    /// So the text is HTML-encoded and wrapped in paragraphs at ingest: one thing in the column, one
    /// sanitiser on the way out, and no branch anywhere asking which provider a row came from.
    /// </summary>
    private static string ToHtml(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return string.Empty;

        var paragraphs = plainText
            .Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Concat(paragraphs.Select(p =>
            $"<p>{HttpUtility.HtmlEncode(p).Replace("\n", "<br>")}</p>"));
    }

    private static string? BuildImageUrl(string? imageId, string size)
        => string.IsNullOrWhiteSpace(imageId) ? null : string.Format(ImageUrlTemplate, size, imageId);

    /// <summary>
    /// One IGDB request, with a single retry when the token is refused.
    ///
    /// The retry is the reason <c>IgdbTokenProvider</c> takes a <c>force</c> flag. A token revoked at
    /// the Twitch end (an admin regenerating the secret, say) is still inside its stated lifetime, so
    /// the cache would go on serving it until it expired — weeks of every call failing with no way
    /// out but a restart.
    /// </summary>
    private async Task<(string? Json, string Reason)> PostAsync(string body, CancellationToken cancellationToken)
    {
        var (clientId, clientSecret) = await settings.GetIgdbCredentialsAsync();
        if (clientId is null || clientSecret is null) return (null, "not-configured");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await tokens.GetTokenAsync(
                clientId, clientSecret, force: attempt > 0, cancellationToken: cancellationToken);
            if (token is null) return (null, "rejected");

            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, GamesEndpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain")
            };
            request.Headers.Add("Client-ID", clientId);
            request.Headers.Add("Authorization", $"Bearer {token}");

            try
            {
                using var response = await client.SendAsync(request, cancellationToken);

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                    continue;

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return (null, "rejected");

                // 4 requests/second, 8 concurrent. Reported distinctly so a caller doing bulk work
                // can back off rather than record the game as unmatched.
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return (null, "rate-limited");

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("IGDB answered {Status}.", (int)response.StatusCode);
                    return (null, "unreachable");
                }

                return (await response.Content.ReadAsStringAsync(cancellationToken), "ok");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning("Could not reach IGDB: {Reason}", ex.GetType().Name);
                return (null, "unreachable");
            }
        }

        return (null, "rejected");
    }

    // ---------------------------------------------------------------- JSON helpers
    //
    // IGDB omits absent fields entirely rather than sending null, so every read is a TryGetProperty.
    // Numeric fields also arrive as whichever JSON number fits — aggregated_rating is fractional,
    // width is not — so the reads are deliberately tolerant about which.

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static double? GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// IGDB dates are Unix epoch seconds, not strings — the target column is declared <c>date</c> and
    /// storing the integer would put "1371168000" on the detail page.
    /// </summary>
    private static DateTime? GetUnixDate(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.Date
            : null;
}
