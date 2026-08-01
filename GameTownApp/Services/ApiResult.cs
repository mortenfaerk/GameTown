using System.Text.Json;

namespace GameTownApp.Services;

/// <summary>
/// Outcome of a write call to the API, with a message suitable for showing to the user.
///
/// The API reports failures in two different shapes: <c>Results.Problem(...)</c> produces a
/// ProblemDetails object (this is how, for example, "role is in use" comes back as a 400), while
/// <c>Results.BadRequest("...")</c> and <c>Results.NotFound("...")</c> produce a bare JSON string.
/// Both are normalised here so callers never have to care which one they got.
/// </summary>
public class ApiResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string? Error { get; init; }

    public static ApiResult Ok(int statusCode = 200) => new() { Success = true, StatusCode = statusCode };

    public static async Task<ApiResult> FromResponse(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
            return new ApiResult { Success = true, StatusCode = status };

        return new ApiResult
        {
            Success = false,
            StatusCode = status,
            Error = ExtractError(await response.Content.ReadAsStringAsync(), status)
        };
    }

    /// <summary>
    /// For responses that never went through HttpClient — the XHR upload path reports a bare status
    /// and body, and should surface errors identically to everything else.
    /// </summary>
    public static ApiResult FromStatus(int status, string? body)
        => status is >= 200 and < 300
            ? new ApiResult { Success = true, StatusCode = status }
            : new ApiResult { Success = false, StatusCode = status, Error = ExtractError(body, status) };

    public static ApiResult Failed(string error, int status = 0)
        => new() { Success = false, StatusCode = status, Error = error };

    private static string ExtractError(string? rawBody, int status)
    {
        var message = TryReadMessage(rawBody);

        // The fallbacks matter as much as the message. 401 and 403 come from the auth middleware
        // with no body at all, and a 413 may not be ours: a reverse proxy over its own
        // client_max_body_size answers with an HTML error page before the request ever reaches
        // GameTown, so there is no JSON to read and the generic wording has to carry it.
        return status switch
        {
            401 => "You are not signed in.",
            403 => "You do not have permission to do that.",
            // 409 always carries our own explanation of what already exists — that IS the message,
            // so there is nothing useful to fall back to beyond saying so.
            409 => message ?? "That already exists.",
            413 => message ?? "That file is too large for the server to accept. "
                            + "If GameTown is behind a reverse proxy, its upload limit is lower than GameTown's.",
            _ => message ?? $"Request failed ({status})."
        };
    }

    /// <summary>
    /// Pulls a human-readable message out of a response body, or null when there is nothing safe to
    /// show.
    /// </summary>
    private static string? TryReadMessage(string? rawBody)
    {
        var body = (rawBody ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind == JsonValueKind.String)
                return doc.RootElement.GetString();

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                    return detail.GetString()!;
                if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                    return title.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Not JSON — a plain-text body is still worth showing, subject to the guards below.
        }

        // Markup is never a message. Two things upstream of this produce HTML on an error path: a
        // reverse proxy's own error page, and MapFallbackToFile handing back the SPA shell for a
        // route that did not match. Both would put a whole web page in the alert banner.
        if (body.StartsWith('<'))
            return null;

        // Nor is anything of essay length; that is a stack trace or a dump, not a sentence.
        return body.Length > 500 ? null : body;
    }
}
