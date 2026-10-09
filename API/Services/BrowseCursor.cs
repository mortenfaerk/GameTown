using System.Text.Json;

namespace API.Services;

/// <summary>
/// Where one batch of the shelf ended: the (Title, Id) of its last game.
///
/// Travels as base64url JSON so it is opaque to the client and safe in a query string without further
/// escaping — titles carry '&amp;', '#' and quotes. Opaque is the point: the client hands it back and
/// never builds one, so the shape can change without a client release.
/// </summary>
public sealed record BrowseCursor(string Title, Guid Id)
{
    public string Encode()
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// False for anything that is not a cursor this server produced. The caller answers 400 rather
    /// than quietly starting from the top, which would hand an infinite scroll its first batch again
    /// and append duplicates forever.
    /// </summary>
    public static bool TryDecode(string? value, out BrowseCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) return false;

        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var decoded = JsonSerializer.Deserialize<BrowseCursor>(Convert.FromBase64String(base64));
            if (decoded is null || decoded.Title is null || decoded.Id == Guid.Empty) return false;

            cursor = decoded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}
