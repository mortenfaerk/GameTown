using System.Net;
using System.Net.Sockets;

namespace API.Services;

/// <summary>What came back, or why nothing did.</summary>
public record ImageFetchResult(bool Success, byte[] Bytes, string Extension, string Reason)
{
    public static ImageFetchResult Failed(string reason) => new(false, [], string.Empty, reason);
    public static ImageFetchResult Ok(byte[] bytes, string extension) => new(true, bytes, extension, "ok");
}

/// <summary>
/// Downloads an image the server was *told* to fetch, under the assumption that whoever named it may
/// be hostile.
///
/// This is a new class of exposure for GameTown. Until box art, every outbound request went to an
/// address baked into the source (RAWG's API). Now a contributor can hand the server a URL and have
/// it fetched — which is server-side request forgery unless each of the following holds. None of them
/// is optional and none is defence in depth; each closes a distinct hole:
///
///   * <b>http/https only.</b> file://, ftp:// and friends would turn this into an arbitrary file read.
///   * <b>No redirects.</b> Every other check here runs against the URL that was supplied. A followed
///     302 is evaluated against nothing at all, which makes the address check below decorative — an
///     attacker just points a public hostname at a redirect to 169.254.169.254.
///   * <b>Public addresses only, checked after resolution.</b> A hostname is not an address, so the
///     literal check has to happen on what DNS returned. This is what keeps the fetcher off loopback,
///     link-local (cloud metadata lives at 169.254.169.254), and the LAN this appliance sits on —
///     which is the interesting target here, because GameTown is *inside* the perimeter.
///   * <b>A byte ceiling, enforced while reading.</b> Content-Length is a claim, not a fact.
///   * <b>Magic-byte sniffing, and the extension derived from it.</b> The result is written into the
///     media directory and served back as a static file from the API's own origin. A stored .html or
///     .svg would be stored XSS against every user of the library — so neither the URL's extension
///     nor the server's Content-Type header is trusted for naming.
///
/// A time-of-check/time-of-use gap remains: DNS is resolved here and again by the socket. It is
/// closed by connecting to the vetted address directly (see <see cref="BuildHandler"/>) rather than
/// by re-resolving, which is what a bare HttpClient would do.
/// </summary>
public class ImageFetcher(IHttpClientFactory httpClientFactory)
{
    /// <summary>
    /// Ten megabytes. Box art is a few hundred kilobytes; this is a runaway guard, not a quality
    /// setting. The upload endpoint applies the same ceiling so both paths agree.
    /// </summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>
    /// The name under which the HttpClient is registered. Configured in DependenciesConfig with the
    /// redirect-blocking, address-validating handler below — asking the factory for a differently
    /// named client would silently get a permissive one.
    /// </summary>
    public const string HttpClientName = "image-fetch";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The handler every image fetch must go through. Registered centrally so there is exactly one
    /// place this policy exists, and no caller can opt out of it by constructing its own client.
    /// </summary>
    public static SocketsHttpHandler BuildHandler() => new()
    {
        // See the class comment: following a redirect evaluates none of the checks below.
        AllowAutoRedirect = false,

        // Closes the DNS rebinding window. The address is vetted here and then connected to
        // directly, so a second resolution cannot return something different from the one approved.
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var permitted = addresses.FirstOrDefault(IsPublic)
                ?? throw new BlockedAddressException(context.DnsEndPoint.Host);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(permitted, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public async Task<ImageFetchResult> FetchAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ImageFetchResult.Failed("no-url");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ImageFetchResult.Failed("malformed-url");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return ImageFetchResult.Failed("unsupported-scheme");

        // A literal address in the URL never reaches DNS, so ConnectCallback's check would not run on
        // it. Checked here as well, which also lets an obviously-internal URL be refused without a
        // connection attempt.
        if (IPAddress.TryParse(uri.Host, out var literal) && !IsPublic(literal))
            return ImageFetchResult.Failed("address-not-permitted");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);

            using var response = await client.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            // A redirect arrives as a normal response because the handler does not follow it. Reported
            // distinctly so the UI can say something more useful than "that did not work".
            if ((int)response.StatusCode is >= 300 and < 400)
                return ImageFetchResult.Failed("redirect-not-followed");

            if (!response.IsSuccessStatusCode)
                return ImageFetchResult.Failed("fetch-failed");

            // The advertised length, when there is one, saves reading a body already known to be too
            // large. The real enforcement is the bounded read below, because this header is a claim.
            if (response.Content.Headers.ContentLength > MaxBytes)
                return ImageFetchResult.Failed("too-large");

            var bytes = await ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(timeout.Token), timeout.Token);

            if (bytes is null)
                return ImageFetchResult.Failed("too-large");

            var extension = SniffExtension(bytes);
            return extension is null
                ? ImageFetchResult.Failed("not-an-image")
                : ImageFetchResult.Ok(bytes, extension);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ImageFetchResult.Failed("timed-out");
        }
        catch (Exception ex) when (IsBlockedAddress(ex))
        {
            // The address check that ran at connect time rather than up front — a *hostname* is not
            // an address, so "localhost" or a public name pointed at 192.168.x.x only fails once DNS
            // has answered. Without this branch that outcome arrives wrapped in an
            // HttpRequestException and reports as "unreachable": still refused, but blamed on the
            // network and described to the user as a fetch failure rather than as the bad address it
            // actually is.
            return ImageFetchResult.Failed("address-not-permitted");
        }
        catch (Exception)
        {
            // No exception detail escapes. This request's destination was chosen by the caller, so the
            // message can carry DNS state, proxy names and internal addresses back to them — the same
            // reasoning as SettingsEndpoints.TestRawgKey.
            return ImageFetchResult.Failed("unreachable");
        }
    }

    /// <summary>
    /// Raised from <see cref="BuildHandler"/>'s connect callback when a host resolves only to
    /// addresses this server will not talk to.
    ///
    /// A distinct type rather than a message to match on: HttpClient wraps whatever the callback
    /// throws, so the only reliable way to tell "you named an internal address" apart from "the
    /// network is down" is the exception's identity.
    /// </summary>
    private sealed class BlockedAddressException(string host)
        : Exception($"'{host}' resolves only to addresses that are not permitted.");

    /// <summary>Whether an exception, or anything it wraps, is the address check refusing to connect.</summary>
    private static bool IsBlockedAddress(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
            if (exception is BlockedAddressException) return true;

        return false;
    }

    /// <summary>Reads up to <see cref="MaxBytes"/>, returning null the moment the source exceeds it.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        using var destination = new MemoryStream();

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;

            // Checked before the write, so the ceiling is never exceeded in memory either.
            if (destination.Length + read > MaxBytes) return null;
            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    /// <summary>
    /// The file extension implied by the leading bytes, or null if this is not an image format we are
    /// willing to store.
    ///
    /// Note what is absent: SVG. It is an image to a user and a script host to a browser, and these
    /// files are served from the API's own origin — an SVG here would be stored XSS with the same
    /// reach as a compromised login page. GIF is absent for a duller reason: nothing produces box art
    /// in it, so accepting it only widens what has to be reasoned about.
    /// </summary>
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string? SniffExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12) return null;

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes[..8].SequenceEqual(PngSignature)) return ".png";

        // WebP: "RIFF" .... "WEBP"
        if (bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return ".webp";

        return null;
    }

    /// <summary>
    /// Whether an address is one this server is willing to connect to on a caller's say-so.
    ///
    /// Written as an allowlist of "not obviously internal" rather than a blocklist of known-bad
    /// ranges, because the interesting target is not the internet — it is the private LAN GameTown is
    /// installed on, and every host on it is reachable from here.
    /// </summary>
    private static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            return octets[0] switch
            {
                0 => false,                                    // "this network"
                10 => false,                                   // RFC1918
                127 => false,                                  // loopback
                169 when octets[1] == 254 => false,            // link-local, incl. cloud metadata
                172 when octets[1] is >= 16 and <= 31 => false, // RFC1918
                192 when octets[1] == 168 => false,            // RFC1918
                100 when octets[1] is >= 64 and <= 127 => false,// CGNAT (RFC6598)
                >= 224 => false,                               // multicast and reserved
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;

            // fc00::/7 — unique local addresses, the IPv6 equivalent of RFC1918.
            var first = address.GetAddressBytes()[0];
            return (first & 0xFE) != 0xFC;
        }

        return false;
    }
}
