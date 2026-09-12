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

        // The wishlist screen's own route. One request ranks the whole queue and bands each row, so
        // the screen can separate "one obvious answer" from "nothing in the library is this game"
        // without opening every row to find out which it is. See MatchConfidenceBands.
        group.MapGet("/suggestions/ranked", GetRanked)
             .Produces<LanRankedQueueContract>(StatusCodes.Status200OK)
             .WithName("GetRankedLanSuggestions")
             .WithDescription("The unmatched queue, ranked against the library and banded by confidence. "
                            + "Filterable by name, LAN event and band; paged.");

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

        // Bulk, and deliberately a separate route rather than a list on /link: a batch can partially
        // fail, so the response shape is genuinely different from a single link and collapsing them
        // would overstate what happened.
        group.MapPost("/link-many", LinkMany)
             .Accepts<LanBulkLinkRequest>("application/json")
             .Produces<LanBulkResult>(StatusCodes.Status200OK)
             .WithName("LinkManyLanSuggestions")
             .WithDescription("Links several suggestions in order, stopping early if the bot stops answering.");

        group.MapPost("/dismiss-many", DismissMany)
             .Accepts<LanBulkDismissRequest>("application/json")
             .Produces<LanBulkResult>(StatusCodes.Status200OK)
             .WithName("DismissManyLanSuggestions")
             .WithDescription("Sets several suggestions aside, or puts them back. Local only.");

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

        // Contributor, not Admin, and that is a revision of the first instinct. The argument for Admin
        // was that a sync spends someone else's rate limit — but a contributor already makes outbound
        // calls to the bot every time they link or unlink, so that was never the real line. Refreshing
        // your own to-do list is not an integration action, and making people wait up to a quarter of
        // an hour to see a suggestion they know was just posted is the kind of friction that gets a
        // screen abandoned.
        //
        // Reading the sync STATUS stays Admin: it reports on how the integration is configured and
        // belongs with the rest of the settings.
        group.MapPost("/sync", Sync)
             .Produces<LanSyncStatusContract>(StatusCodes.Status200OK)
             .WithName("SyncLanSuggestions")
             .WithDescription("Runs a sync now, rather than waiting for the configured interval.");

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

        // Admin: the status panel reports on how the integration is configured — the poll interval,
        // whether credentials are stored — which is settings-page business rather than library business.
        var admin = app.MapGroup("/lan")
            .RequireAuthorization("Admin")
            .WithTags("LAN suggestions");

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

    /// <param name="page">1-based. Out-of-range values are clamped rather than rejected — this is a
    /// screen's own paging, not an address anyone types.</param>
    private static async Task<IResult> GetRanked(
        LanSuggestionService suggestions,
        CancellationToken cancellationToken,
        string? q = null,
        string? lan = null,
        string? confidence = null,
        int page = 1,
        int pageSize = 50)
        => Results.Ok(await suggestions.GetRankedAsync(q, lan, confidence, page, pageSize, cancellationToken));

    /// <summary>
    /// Both counts in one request. The sidebar link needs "how many are waiting" and "does this
    /// install use the bot at all", and used to answer the second by downloading every suggestion.
    /// </summary>
    private static async Task<IResult> GetCount(
        LanSuggestionService suggestions, CancellationToken cancellationToken)
        => Results.Ok(new LanCountContract
        {
            Unmatched = await suggestions.CountUnmatchedAsync(cancellationToken),
            Total = await suggestions.CountAllAsync(cancellationToken),
        });

    /// <summary>
    /// Always 200, even for a refusal — the body carries the reason code, in the same way the box-art
    /// and metadata endpoints do.
    /// </summary>
    private static async Task<IResult> Link(
        LanLinkRequest request, LanSuggestionService suggestions, CancellationToken cancellationToken)
        => Results.Ok(await suggestions.LinkAsync(request.RemoteId, request.GameId, cancellationToken));

    private static async Task<IResult> LinkMany(
        LanBulkLinkRequest request, LanSuggestionService suggestions, CancellationToken cancellationToken)
        => Results.Ok(await suggestions.LinkManyAsync(request.Links, cancellationToken));

    private static async Task<IResult> DismissMany(
        LanBulkDismissRequest request, LanSuggestionService suggestions, CancellationToken cancellationToken)
        => Results.Ok(await suggestions.DismissManyAsync(request.RemoteIds, request.Dismissed, cancellationToken));

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
