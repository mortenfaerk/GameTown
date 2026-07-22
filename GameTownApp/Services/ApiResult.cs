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
            Error = await ExtractErrorAsync(response, status)
        };
    }

    private static async Task<string> ExtractErrorAsync(HttpResponseMessage response, int status)
    {
        if (status == 401) return "You are not signed in.";
        if (status == 403) return "You do not have permission to do that.";

        var body = (await response.Content.ReadAsStringAsync()).Trim();
        if (string.IsNullOrEmpty(body))
            return $"Request failed ({status}).";

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind == JsonValueKind.String)
                return doc.RootElement.GetString() ?? body;

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
            // Not JSON — fall through and show the raw body.
        }

        return body;
    }
}
