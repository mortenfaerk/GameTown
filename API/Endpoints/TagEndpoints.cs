using API.Services;

namespace API.Endpoints;

public static class TagEndpoints
{
    public static void AddTagEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/tags")
            .WithTags("Tags")
            .WithDescription("Manual tags describing how a game is played.");

        // Anonymous, like browsing. The tag list drives the library's filter bar, which anyone on the
        // LAN can use — gating it would leave a signed-out visitor with a filter bar full of nothing.
        // The global fallback policy requires authentication, so this has to opt out explicitly.
        //
        // NB: no .Accepts<T>() on the GET routes. See the comment in GamesEndpoints — on a GET it
        // makes the route unmatchable, and under SPA-fallback hosting that returns 200 text/html
        // rather than a 404, so the caller parses a web page as JSON.
        group.MapGet("/", GetTags)
             .AllowAnonymous()
             .Produces<IEnumerable<TagContract>>(StatusCodes.Status200OK)
             .WithName("GetTags")
             .WithDescription("Every tag in use, with the number of games carrying it.");

        group.MapGet("/game/{id}", GetGameTags)
             .AllowAnonymous()
             .Produces<IEnumerable<TagContract>>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest)
             .WithName("GetGameTags")
             .WithDescription("The tags on one game.");

        group.MapPut("/game/{id}", SetGameTags)
             .Accepts<SetGameTagsRequest>("application/json")
             .RequireAuthorization("Contributor")
             .Produces<IEnumerable<TagContract>>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status404NotFound)
             .WithName("SetGameTags")
             .WithDescription("Replaces a game's tags. Unknown names are created; names are matched case-insensitively.");
    }

    /// <summary>
    /// <paramref name="quick"/> filters to the one-click tags the editor offers as buttons. A query
    /// parameter rather than a second route, because it is the same list with a predicate applied.
    /// </summary>
    private static async Task<IResult> GetTags(TagService tags, bool quick = false)
        => Results.Ok(await tags.GetAllAsync(quick));

    private static async Task<IResult> GetGameTags(string id, TagService tags)
    {
        if (!Guid.TryParse(id, out var gameId))
            return Results.BadRequest("Invalid game ID format.");

        return Results.Ok(await tags.GetGameTagsAsync(gameId));
    }

    private static async Task<IResult> SetGameTags(string id, SetGameTagsRequest request, TagService tags)
    {
        if (!Guid.TryParse(id, out var gameId))
            return Results.BadRequest("Invalid game ID format.");

        // An empty list is legitimate — it means "this game has no tags" — so there is no guard
        // against it here. Only a null body is a malformed request, and the binder rejects that.
        try
        {
            return Results.Ok(await tags.SetGameTagsAsync(gameId, request.Names));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }
}
