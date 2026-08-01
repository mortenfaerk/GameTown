using API.Services;
using API.Services.Archives;

namespace API.Endpoints;

/// <summary>
/// Writing a game's instructions into its own archive, and taking them out again.
///
/// Its own group rather than a field on the update patch, for the same reason tags and box art have
/// their own: this touches a file on disk, and it can fail — on a damaged archive, a read-only share,
/// a format that cannot carry it — in ways that must not retract a save that already succeeded.
/// </summary>
public static class GuideEndpoints
{
    public static void AddGuideEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/guide")
            .RequireAuthorization("Contributor")
            .WithTags("Archive guide")
            .WithDescription("Writing the instructions into the downloadable archive itself.");

        group.MapPut("/{id}", SetGuide)
             .Accepts<SetGuideRequest>("application/json")
             .Produces<GameContract>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status404NotFound)
             .WithName("SetArchiveGuide")
             .WithDescription("Adds or removes GameTownGuide.txt inside the game's archive.");
    }

    /// <summary>
    /// One route taking the desired state, rather than a POST and a DELETE.
    ///
    /// The caller is always saying "make the archive match the toggle", and expressing that as a
    /// single idempotent PUT means a retry after a dropped response cannot leave the archive and the
    /// flag disagreeing about which way round they are.
    /// </summary>
    private static async Task<IResult> SetGuide(
        string id, SetGuideRequest request, ArchiveGuideService guides, GTGamesService games,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var gameId))
            return Results.BadRequest("Invalid game ID format.");

        try
        {
            var result = await guides.ApplyAsync(gameId, request.Baked, cancellationToken);

            return result.Success
                ? Results.Ok(await games.GetGameById(gameId))
                : Results.BadRequest(Describe(result.Reason));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static string Describe(string reason) => reason switch
    {
        "unsupported-format" =>
            "Only ZIP archives can have the instructions written into them. A ZIP keeps its index at "
            + "the end, so one file can be added without repacking the whole archive; RAR and 7z cannot "
            + "be changed without rebuilding them.",

        "not-a-zip" =>
            "That archive is named .zip but could not be read as one, so nothing was written to it.",

        "archive-missing" =>
            "The archive file for this game could not be found on the server.",

        // "write-failed", and anything added later.
        _ => "The instructions could not be written into the archive.",
    };
}
