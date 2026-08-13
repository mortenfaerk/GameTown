using API.Services.Metadata;
using GameTown.Contracts.Games;

namespace API.Endpoints;

public static class MetadataRelinkEndpoints
{
    public static void AddMetadataRelinkEndpoints(this WebApplication app)
    {
        // Admin, not Contributor. Re-linking rewrites what a game in someone else's library points
        // at, and the bulk propose call spends the provider's rate limit in proportion to the size of
        // the library — neither is a contributor-level action.
        var group = app.MapGroup("/metadata-relink")
            .RequireAuthorization("Admin")
            .WithTags("Metadata")
            .WithDescription("Moves library entries from a retired metadata provider to the current one.");

        group.MapGet("/candidates", GetCandidates)
             .Produces<List<RelinkCandidateContract>>(StatusCodes.Status200OK)
             .WithName("GetRelinkCandidates")
             .WithDescription("Games whose metadata came from a provider other than the configured one.");

        group.MapPost("/propose", Propose)
             .Accepts<RelinkProposeRequest>("application/json")
             .Produces<List<RelinkProposalContract>>(StatusCodes.Status200OK)
             .WithName("ProposeRelinks")
             .WithDescription("Searches the current provider for each game and returns proposed pairings. Writes nothing.");

        group.MapPost("/apply", Apply)
             .Accepts<RelinkApplyRequest>("application/json")
             .Produces<RelinkApplyResult>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest)
             .WithName("ApplyRelinks")
             .WithDescription("Applies confirmed pairings. Never touches box art, tags or instructions.");
    }

    private static async Task<IResult> GetCandidates(
        MetadataRelinkService relink, CancellationToken cancellationToken)
    {
        var candidates = await relink.GetCandidatesAsync(cancellationToken);

        return Results.Ok(candidates.Select(c => new RelinkCandidateContract
        {
            GameId = c.GameId,
            Title = c.Title,
            MetadataName = c.MetadataName,
            Provider = c.Provider,
            Released = c.Released
        }).ToList());
    }

    private static async Task<IResult> Propose(
        RelinkProposeRequest request, MetadataRelinkService relink, CancellationToken cancellationToken)
    {
        if (request.GameIds.Count == 0)
            return Results.BadRequest("No games were selected.");

        var proposals = await relink.ProposeAsync(request.GameIds, cancellationToken);

        return Results.Ok(proposals.Select(p => new RelinkProposalContract
        {
            GameId = p.GameId,
            Title = p.Title,
            IsConfident = p.IsConfident,
            Reason = p.Reason,
            MatchExternalId = p.Match?.ExternalId,
            MatchName = p.Match?.Name,
            MatchReleased = p.Match?.Released,
            MatchThumbnailUrl = p.Match?.ThumbnailUrl
        }).ToList());
    }

    /// <summary>
    /// Applies each confirmed pairing independently.
    ///
    /// One failure does not abandon the rest: these are separate games and an admin who ticked twenty
    /// should not lose nineteen because the twentieth is not in the catalogue any more. The result
    /// reports per game, so the screen can show exactly what happened rather than a single verdict
    /// over a batch.
    /// </summary>
    private static async Task<IResult> Apply(
        RelinkApplyRequest request, MetadataRelinkService relink, CancellationToken cancellationToken)
    {
        if (request.Pairings.Count == 0)
            return Results.BadRequest("No pairings were confirmed.");

        var result = new RelinkApplyResult();

        foreach (var pairing in request.Pairings)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await relink.ApplyAsync(pairing.GameId, pairing.ExternalId, cancellationToken);
                result.Applied.Add(pairing.GameId);
            }
            catch (KeyNotFoundException)
            {
                result.Failed.Add(new RelinkFailure { GameId = pairing.GameId, Reason = "not-found" });
            }
            catch (Exception)
            {
                // No exception detail: this reports on an outbound request and on server paths.
                result.Failed.Add(new RelinkFailure { GameId = pairing.GameId, Reason = "unreachable" });
            }
        }

        return Results.Ok(result);
    }
}
