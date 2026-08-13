using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace API.Services.Metadata;

/// <summary>
/// Holds the Twitch OAuth token IGDB requires, and refreshes it when it expires.
///
/// IGDB does not have an API key. It authenticates with a Twitch application's client-credentials
/// grant: POST the client id and secret to id.twitch.tv, get a bearer token back, send it alongside
/// the client id on every IGDB call. Tokens last about sixty days.
///
/// ---------------------------------------------------------------- the tension this class resolves
///
/// Two rules in this codebase pull in opposite directions here:
///
///   * Settings must be read PER CALL. <c>SettingsService</c> has no cache, deliberately, because
///     both <c>RAWGService</c> and <c>FileService</c> once captured their configuration as
///     constructor arguments — so the admin page saved, the database updated, and the running service
///     went on using the value it took at boot. The settings UI looked broken because it was.
///   * The token must NOT be re-fetched per call. It is valid for two months; asking Twitch for a new
///     one before every search would add a round trip to every keystroke in the picker and would
///     eventually be throttled.
///
/// Resolved by caching the token but <b>keying the cache on a hash of the credentials that produced
/// it</b>. The credentials are still read from the database on every call, exactly as the rule
/// requires; only the derived token is reused, and only while the credentials it was derived from are
/// still the current ones. Change either in the admin UI and the next call misses the cache and
/// re-authenticates. There is nothing to invalidate and no way for a stale token to outlive the
/// secret it came from.
///
/// A hash rather than the credentials themselves so a memory dump of this object is not a copy of the
/// client secret.
///
/// Registered as a SINGLETON. A scoped cache would be no cache at all — one token fetch per request
/// is the behaviour this class exists to avoid — which is the inverse of the mistake
/// <c>SettingsService</c>'s comment warns about, and worth stating because the two look alike.
/// </summary>
public class IgdbTokenProvider(IHttpClientFactory httpClientFactory, ILogger<IgdbTokenProvider> logger)
{
    public const string HttpClientName = "igdb-auth";
    private const string TokenEndpoint = "https://id.twitch.tv/oauth2/token";

    /// <summary>
    /// Refresh this long before the stated expiry. Sixty days makes the exact margin unimportant, but
    /// a token that expires between the check and the call is a 401 the caller has to handle, and
    /// there is no reason to arrange for one.
    /// </summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _credentialFingerprint;
    private string? _token;
    private DateTimeOffset _expiresAt;

    /// <summary>
    /// A bearer token for these credentials, from cache when possible.
    ///
    /// <paramref name="force"/> is how a 401 is handled: the caller retries once with a token it has
    /// explicitly discarded. Without it a token revoked at the Twitch end would keep being served
    /// from cache until its stated expiry — potentially weeks of every call failing.
    /// </summary>
    public async Task<string?> GetTokenAsync(
        string clientId, string clientSecret, bool force = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return null;

        var fingerprint = Fingerprint(clientId, clientSecret);

        if (!force && TryReadCache(fingerprint, out var cached))
            return cached;

        // One caller authenticates; the rest wait and take the result. Without the gate, the first
        // page load after a restart fires a token request per concurrent call — the picker alone can
        // issue several — and Twitch sees a burst of identical grants.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the lock: by the time a queued caller acquires it, the one ahead has
            // usually already stored a perfectly good token. Skipping this makes the gate serialise
            // the fetches instead of collapsing them.
            if (!force && TryReadCache(fingerprint, out cached))
                return cached;

            var fetched = await RequestTokenAsync(clientId, clientSecret, cancellationToken);
            if (fetched is null) return null;

            _credentialFingerprint = fingerprint;
            _token = fetched.Value.Token;
            _expiresAt = DateTimeOffset.UtcNow.Add(fetched.Value.Lifetime);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryReadCache(string fingerprint, out string? token)
    {
        token = null;

        // The fingerprint comparison is the whole point: a token minted from credentials that have
        // since been edited in the settings UI is not a cache hit, it is a stale secret.
        if (_token is null || _credentialFingerprint != fingerprint) return false;
        if (DateTimeOffset.UtcNow >= _expiresAt - ExpiryMargin) return false;

        token = _token;
        return true;
    }

    private async Task<(string Token, TimeSpan Lifetime)?> RequestTokenAsync(
        string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

        // Form-encoded body rather than the query string Twitch also accepts: a client secret in a
        // URL is a secret in every proxy log and every exception message that carries the request URI.
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "client_credentials"
        });

        try
        {
            using var response = await client.PostAsync(TokenEndpoint, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Status only. The body of a failed token request echoes back detail that belongs in
                // neither the log nor an error message shown to an admin.
                logger.LogWarning("IGDB token request refused with status {Status}.", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("access_token", out var tokenElement)) return null;
            var token = tokenElement.GetString();
            if (string.IsNullOrEmpty(token)) return null;

            var seconds = document.RootElement.TryGetProperty("expires_in", out var expiresElement)
                          && expiresElement.TryGetInt64(out var value)
                ? value
                // Twitch always sends expires_in; if it ever stops, an hour is short enough to be
                // harmless and long enough not to hammer the endpoint.
                : 3600;

            return (token, TimeSpan.FromSeconds(seconds));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Could not reach the IGDB token endpoint: {Reason}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// An opaque, stable identifier for a credential pair. Never reversible to the secret, and the
    /// separator keeps ("ab", "c") from colliding with ("a", "bc").
    /// </summary>
    private static string Fingerprint(string clientId, string clientSecret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{clientId} {clientSecret}")));
}
