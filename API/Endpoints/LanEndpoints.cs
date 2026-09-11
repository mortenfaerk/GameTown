using API.Services.Lan;
using GameTown.Contracts.Lan;

namespace API.Endpoints;

public static class LanEndpoints
{
    public static void AddLanEndpoints(this WebApplication app)
    {
        // Contributor, not Admin. Deciding that "Wardogs" is the game in the library is exactly the
        // judgement a contributor already makes when they tag and describe an upload — and the
        // wishlist is their to-do list, so gating it at Admin would put the work behind the person
        // least likely to be doing it. Running the sync and reading its status stay Admin: those are
        // about the integration rather than about the library.
        var group = app.MapGroup("/lan")
            .RequireAuthorization("Contributor")
            .WithTags("LAN suggestions")
            .WithDescription("Game suggestions pulled from the LAN Discord bot, and the links to library entries.");

        // NB: no .Accepts<T>() on the GET routes here, and do not add any. Accepts describes a request
        // BODY and applies a content-type constraint to the endpoint; a GET carries neither, so the
        // route becomes unmatchable, falls through to MapFallbackToFile and answers 200 text/html —
        // success, as far as the caller can tell, until it tries to parse the SPA shell as JSON. See
        // the same note in GamesEndpoints.
        group.MapGet("/suggestions", GetSuggestions)
             .Produces<List<LanSuggestionContract>>(StatusCodes.Status200OK)
             .WithName("GetLanSuggestions")
             .WithDescription("Suggestions, filtered by state: unmatched, matched, dismissed or all.");

        group.MapGet("/suggestions/{remoteId:long}/candidates", GetCandidates)
             .Produces<List<LanCandidateContract>>(StatusCodes.Status200OK)
             .WithName("GetLanCandidates")
             .WithDescription("Library entries ranked as possible matches for one suggestion. Advisory only.");

        group.MapGet("/count", GetCount)
             .Produces<LanCountContract>(StatusCodes.Status200OK)
             .WithName("GetLanUnmatchedCount")
             .WithDescription("How many suggestions are waiting on a human.");

        // Every mutation below is a POST, including the ones that read like toggles. SameSite=Lax is
        // the only CSRF mitigation this application has, and it holds only for as long as no GET
        // changes state. Nothing in the build will complain if that is broken. See SECURITY-NOTES.md.
        group.MapPost("/link", Link)
             .Accepts<LanLinkRequest>("application/json")
             .Produces<LanLinkResult>(StatusCodes.Status200OK)
             .WithName("LinkLanSuggestion")
             .WithDescription("Links a suggestion to a library entry and pushes it to the bot. "
                            + "A game other suggestions already point at is fine — bindings are additive.");

        group.MapPost("/unlink", Unlink)
             .Accepts<LanRemoteIdRequest>("application/json")
             .Produces<LanLinkResult>(StatusCodes.Status200OK)
             .WithName("UnlinkLanSuggestion")
             .WithDescription("Undoes a link, deleting the catalogue entry when GameTown created it.");

        group.MapPost("/dismiss", Dismiss)
             .Accepts<LanDismissRequest>("application/json")
             .Produces<LanLinkResult>(StatusCodes.Status200OK)
             .WithName("DismissLanSuggestion")
             .WithDescription("Marks a suggestion as never going to be matched, or restores it. Local only.");

        // The shelf chips are rendered for anonymous visitors, so this cannot sit behind the group's
        // Contributor policy. It carries LAN event names and game counts — the same information the
        // filtered shelf already shows anyone who can reach the host. Worth knowing that the names
        // themselves come from Discord and are therefore published by this route; see SECURITY-NOTES.
        app.MapGet("/lan/events", GetEvents)
           .AllowAnonymous()
           .Produces<List<LanEventContract>>(StatusCodes.Status200OK)
           .WithTags("LAN suggestions")
           .WithName("GetLanEvents")
           .WithDescription("LAN events with at least one library game suggested for them.");

        // Admin: running a sync spends someone else's rate limit, and the status panel reports on a
        // configured outbound integration rather than on the library.
        var admin = app.MapGroup("/lan")
            .RequireAuthorization("Admin")
            .WithTags("LAN suggestions");

        admin.MapPost("/sync", Sync)
             .Produces<LanSyncStatusContract>(StatusCodes.Status200OK)
             .WithName("SyncLanSuggestions")
             .WithDescription("Runs a sync now, rather than waiting for the configured interval.");

        admin.MapGet("/status", GetStatus)
             .Produces<LanSyncStatusContract>(StatusCodes.Status200OK)
             .WithName("GetLanSyncStatus")
             .WithDescription("What the last sync did, and whether polling is configured.");
    }

    private static async Task<IResult> GetSuggestions(
        LanSuggestionService suggestions, CancellationToken cancellationToken, string? state = null)
        => Results.Ok(await suggestions.GetAsync(state ?? "all", cancellationToken));

    private static async Task<IResult> GetCandidates(
        long remoteId, LanSuggestionService suggestions, CancellationToken cancellationToken)
        => Results.Ok(await suggestions.GetCandidatesAsync(remoteId, cancellationToken));

    private static async Task<IResult> GetCount(
        LanSuggestionService suggestions, CancellationToken cancellationToken)
        => Results.Ok(new LanCountContract
        {
            Unmatched = await suggestions.CountUnmatchedAsync(cancellationToken)
        });

    /// <summary>
    /// Always 200, even for a refusal — the body carries the reason code, in the same way the box-art
    /// and metadata endpoints do.
    /// </summary>
    private static async Task<IResult> Link(
        LanLinkRequest request, LanSuggestionService suggestions, CancellationToken cancellationToken)
        => Results.Ok(await suggestions.LinkAsync(request.RemoteId, request.GameId, cancellationToken));

    private static async Task<IResult> Unlink(
        LanRemoteIdRequest request, LanSuggestionService suggestions, CancellationToken cancellationToken)
        => Results.Ok(await suggestions.UnlinkAsync(request.RemoteId, cancellationToken));

    private static async Task<IResult> Dismiss(
        LanDismissRequest request, LanSuggestionService suggestions, CancellationToken cancellationToken)
        => Results.Ok(await suggestions.DismissAsync(request.RemoteId, request.Dismissed, cancellationToken));

    private static async Task<IResult> GetEvents(
        LanSuggestionService suggestions, CancellationToken cancellationToken)
        => Results.Ok(await suggestions.GetEventsAsync(cancellationToken));

    /// <summary>
    /// Runs a sync and returns the resulting status, so the screen needs no second request to show
    /// what happened.
    /// </summary>
    private static async Task<IResult> Sync(
        LanSuggestionService suggestions, CancellationToken cancellationToken)
    {
        await suggestions.SyncAsync(cancellationToken);
        return Results.Ok(await suggestions.GetStatusAsync(cancellationToken));
    }

    private static async Task<IResult> GetStatus(
        LanSuggestionService suggestions, CancellationToken cancellationToken)
        => Results.Ok(await suggestions.GetStatusAsync(cancellationToken));
}
