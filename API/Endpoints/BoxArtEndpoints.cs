using API.Services;
using API.Services.BoxArt;

namespace API.Endpoints;

/// <summary>
/// Choosing the portrait cover a game appears under on the shelf.
///
/// Its own group rather than routes hung off /GTGames, so that nothing here can shadow or be shadowed
/// by "/GTGames/{id}" — that route already had to be defended from "/download/{id}" and
/// "/upload-limits" by literal-segment precedence, and adding a third pattern to the same prefix is
/// how that eventually goes wrong.
/// </summary>
public static class BoxArtEndpoints
{
    public static void AddBoxArtEndpoints(this WebApplication app)
    {
        // Contributor throughout, including the search. Two reasons: it spends the provider's API key,
        // and it makes the server issue an outbound request with a caller-influenced string — the same
        // pair of concerns that gates /meta and /settings/test-rawg-key.
        //
        // NB: no .Accepts<T>() on the GET below. Accepts describes a request body and constrains the
        // endpoint's content type; a GET carries neither, and adding one makes the route unmatchable
        // by any normal client — which under SPA-fallback hosting returns 200 text/html rather than a
        // 404. See the comment in GamesEndpoints.
        var group = app.MapGroup("/boxart")
            .RequireAuthorization("Contributor")
            .WithTags("Box art")
            .WithDescription("Finding and setting the cover art a game is shelved under.");

        group.MapGet("/search", SearchBoxArt)
             .Produces<BoxArtSearchResult>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest)
             .WithName("SearchBoxArt")
             .WithDescription("Candidate cover art for a title, from the configured artwork provider.");

        group.MapPost("/{id}", SetBoxArtFromUrl)
             .Accepts<SetBoxArtRequest>("application/json")
             .Produces<GameContract>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status413PayloadTooLarge)
             .Produces(StatusCodes.Status502BadGateway)
             .WithName("SetBoxArtFromUrl")
             .WithDescription("Downloads an image and stores it locally as the game's box art.");

        group.MapPost("/{id}/upload", UploadBoxArt)
             .Accepts<IFormFile>("multipart/form-data")
             .Produces<GameContract>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status413PayloadTooLarge)
             .WithName("UploadBoxArt")
             .WithDescription("Uploads an image file to use as the game's box art.")
             // Same reasoning as the archive upload: this is a cookie-authenticated multipart POST
             // from the SPA, which holds no antiforgery token. SameSite=Lax is the CSRF control.
             .DisableAntiforgery();

        group.MapDelete("/{id}", ClearBoxArt)
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status404NotFound)
             .WithName("ClearBoxArt")
             .WithDescription("Removes the override, falling back to the RAWG image.");
    }

    private static async Task<IResult> SearchBoxArt(
        string title, BoxArtService service, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest("A title is required.");

        // Always 200, even for "not-configured" and "unreachable". Those are states the picker has to
        // explain to a contributor, not errors in the request they made — and an error status would
        // put them in the generic failure branch of the client instead.
        return Results.Ok(await service.SearchAsync(title, cancellationToken));
    }

    private static async Task<IResult> SetBoxArtFromUrl(
        string id, SetBoxArtRequest request, BoxArtService service, GTGamesService games,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var gameId))
            return Results.BadRequest("Invalid game ID format.");

        try
        {
            var result = await service.SetFromUrlAsync(gameId, request.Url, cancellationToken);
            return result.Success
                ? Results.Ok(await games.GetGameById(gameId))
                : FailureResult(result.Reason);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> UploadBoxArt(
        string id, HttpContext context, BoxArtService service, GTGamesService games,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var gameId))
            return Results.BadRequest("Invalid game ID format.");

        if (!context.Request.HasFormContentType)
            return Results.BadRequest("Expected a multipart form containing an image.");

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null)
            return Results.BadRequest("No image was included in the request.");

        try
        {
            using var stream = file.OpenReadStream();
            var result = await service.SetFromUploadAsync(gameId, stream, cancellationToken);
            return result.Success
                ? Results.Ok(await games.GetGameById(gameId))
                : FailureResult(result.Reason);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> ClearBoxArt(
        string id, BoxArtService service, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var gameId))
            return Results.BadRequest("Invalid game ID format.");

        try
        {
            await service.ClearAsync(gameId, cancellationToken);
            return Results.NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Turns a fetcher reason code into a status and a sentence a contributor can act on.
    ///
    /// The codes are deliberately coarse and carry no detail about the outbound request — see
    /// ImageFetcher — so the mapping is where the words get added, once, rather than at each call site.
    /// </summary>
    private static IResult FailureResult(string reason) => reason switch
    {
        "too-large" => Results.Json(
            $"That image is larger than {ImageFetcher.MaxBytes / (1024 * 1024)} MB. Box art should be a "
            + "cover, not a poster print.",
            statusCode: StatusCodes.Status413PayloadTooLarge),

        "not-an-image" => Results.BadRequest(
            "That is not a JPEG, PNG or WebP image. (SVG is deliberately not accepted — it can carry "
            + "script, and these files are served from this server's own address.)"),

        "address-not-permitted" => Results.BadRequest(
            "That address is on a private or local network, which this server will not fetch from."),

        "unsupported-scheme" => Results.BadRequest("Only http and https image links can be fetched."),
        "malformed-url" or "no-url" => Results.BadRequest("That is not a valid image link."),

        "redirect-not-followed" => Results.BadRequest(
            "That link redirects elsewhere, which is not followed. Use the address of the image itself."),

        "timed-out" => Results.Json("Fetching that image took too long.",
            statusCode: StatusCodes.Status502BadGateway),

        // "fetch-failed", "unreachable", and anything added later.
        _ => Results.Json("That image could not be fetched.",
            statusCode: StatusCodes.Status502BadGateway),
    };
}
