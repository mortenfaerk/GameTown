using EFModel.Models;
using GameTown.Contracts.Lan;
using Microsoft.EntityFrameworkCore;

namespace API.Services.Lan;

/// <summary>
/// Keeps GameTown's mirror of the LAN bot's suggestions in step, and owns every rule about linking a
/// suggestion to a library entry.
///
/// A row carries two different claims, and keeping them apart is what the whole design rests on:
///
///  * <c>GameId</c> — what GAMETOWN worked out this suggestion is. The badge, the ?lan= filter and the
///    wishlist all read this. Set whenever GameTown can identify the game, whether or not the bot ever
///    hears about it.
///  * <c>RemoteMatchId</c> — what the BOT actually has this suggestion pointing at. Often the same
///    thing; sometimes a catalogue entry the crew made by hand, which GameTown cannot take over.
///
/// The bot is the authority on the second. <see cref="SyncAsync"/> reconciles to it rather than
/// re-asserting local state, which is what makes this self-healing when the crew changes something on
/// their side instead of slowly drifting out of agreement with the system it exists to mirror.
///
/// HOW THE BOT ACTUALLY BINDS, per its own OpenAPI document (Catalogue API v1.4.0):
///
///  1. A game push (<c>PUT /games/{matchId}</c>) writes the entry's title/url/boxArtUrl and, as a
///     side effect, adopts any currently-unmatched suggestion whose name case-insensitively equals
///     the pushed title. It never overwrites an existing match.
///  2. A direct bind (<c>PUT /suggestions/{id}/match</c>) points one suggestion at a matchId
///     unconditionally — re-pointing an already-bound suggestion succeeds, last-writer-wins. The
///     matchId must already have a catalogue row.
///  3. A direct unbind (<c>DELETE /suggestions/{id}/match</c>) clears just that suggestion's match,
///     leaving the catalogue entry and every other suggestion bound to it untouched.
///
/// So several suggestions can share one game (1), and — unlike before this API version — a
/// suggestion the crew already bound elsewhere CAN be claimed by GameTown, by calling (2) directly
/// rather than relying on (1)'s name-matching side effect.
/// </summary>
public class LanSuggestionService(
    DatabaseContext context,
    LanBotClient bot,
    SettingsService settings,
    LanSyncState state,
    ILogger<LanSuggestionService> logger)
{
    /// <summary>
    /// Pulls from the bot, reconciles, matches what it can, and pushes.
    ///
    /// The four phases run in this order for a reason — matching before reconciling would decide
    /// against local state the bot has already contradicted, and pushing before matching would push
    /// last run's answer.
    /// </summary>
    public async Task<string> SyncAsync(CancellationToken cancellationToken = default)
    {
        // Returned BEFORE the try, so the finally below cannot mark the run that is actually in
        // flight as finished — which would let a third caller straight through into the same race.
        if (!state.TryBeginRun()) return "busy";

        var seen = 0;
        var matched = 0;
        var bound = 0;
        var reason = "ok";

        try
        {
            var (remote, pullReason) = await bot.GetSuggestionsAsync(cancellationToken);

            // A failed pull must not reach reconciliation. An empty list because the bot is
            // unreachable looks identical to every suggestion having been withdrawn, and phase 2
            // would faithfully unbind the entire library on the strength of it.
            if (pullReason != "ok") return reason = pullReason;

            seen = remote.Count;

            await MirrorAsync(remote, cancellationToken);
            await ReconcileAsync(remote, cancellationToken);
            (matched, bound) = await MatchAndPushAsync(cancellationToken);

            return reason;
        }
        catch (Exception exception)
        {
            // A sync runs on a timer with nobody watching, so it must never be the thing that takes
            // the application down. The reason code is what the screen shows; the type goes to the log.
            logger.LogError(exception, "The LAN bot sync failed.");
            return reason = "unreachable";
        }
        finally
        {
            state.MarkFinished(reason, seen, matched, bound);
        }
    }

    /// <summary>
    /// Phase 1 — every suggestion the bot reports exists locally, with current name, event and played
    /// flag.
    ///
    /// Rows are never deleted when they stop being reported. <c>Dismissed</c> is a human judgement
    /// that exists nowhere else and <c>FirstSeenUtc</c> outlives anything the bot knows, so a
    /// suggestion that disappears and returns must come back as the same row rather than as new work.
    /// </summary>
    private async Task MirrorAsync(List<LanBotSuggestion> remote, CancellationToken cancellationToken)
    {
        var ids = remote.Select(r => r.Id).ToList();
        var existing = await context.LanSuggestions
            .Where(s => ids.Contains(s.RemoteId))
            .ToDictionaryAsync(s => s.RemoteId, cancellationToken);

        var now = DateTime.UtcNow;

        foreach (var item in remote)
        {
            if (existing.TryGetValue(item.Id, out var row))
            {
                row.Name = item.Name;
                row.LanEventName = item.LanEventName;
                row.Played = item.Played;
                row.LastSeenUtc = now;
                continue;
            }

            context.LanSuggestions.Add(new LanSuggestion
            {
                RemoteId = item.Id,
                Name = item.Name,
                LanEventName = item.LanEventName,
                Played = item.Played,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Phase 2 — local binding state is made to agree with what the bot reports, per suggestion.
    ///
    /// The bot is the authority here and this never argues with it:
    ///
    ///  * it reports a binding we do not have → adopt it, and adopt the GameId too when the matchId is
    ///    a game we hold (the crew may have linked it by hand inside the bot);
    ///  * it reports none for a row that claims one → clear <c>RemoteMatchId</c> and leave
    ///    <c>GameId</c> alone. GameTown still knows what the game is; it has merely stopped being what
    ///    the bot points at. Clearing both would drop the row off the shelf and out of the badge for
    ///    no reason a visitor could see.
    ///
    /// Several suggestions pointing at one matchId is normal and is left exactly as reported —
    /// bindings at the far end are additive, and a game asked for under three spellings really does
    /// carry three of them.
    /// </summary>
    private async Task ReconcileAsync(List<LanBotSuggestion> remote, CancellationToken cancellationToken)
    {
        var reported = remote
            .Where(r => r.GameTownMatchId is not null)
            .ToDictionary(r => r.Id, r => r.GameTownMatchId!.Value);

        var known = await context.GameTownGames.Select(g => g.Id).ToListAsync(cancellationToken);
        var knownGames = known.ToHashSet();

        foreach (var row in await context.LanSuggestions.ToListAsync(cancellationToken))
        {
            if (!reported.TryGetValue(row.RemoteId, out var matchId))
            {
                row.RemoteMatchId = null;
                continue;
            }

            row.RemoteMatchId = matchId;

            // A matchId we do not recognise is not an error: the crew's own catalogue entries carry
            // GUIDs that were never GameTown game ids. Record the binding, claim no game for it.
            if (row.GameId is null && knownGames.Contains(matchId))
            {
                row.GameId = matchId;
                row.LinkSource = "remote";
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Phases 3 and 4 — identify games for unmatched suggestions, then push the ones that can be
    /// pushed.
    ///
    /// The two are separate counts because they legitimately differ. Matching records what GameTown
    /// knows and always happens; pushing is skipped when the game already has a bound suggestion,
    /// because binding it would overwrite that one and the next tick would overwrite it back — a
    /// flip-flop that logs nothing and shows up only as a Discord link that keeps changing.
    /// </summary>
    private async Task<(int Matched, int Bound)> MatchAndPushAsync(CancellationToken cancellationToken)
    {
        var candidates = await context.LanSuggestions
            .Where(s => !s.Dismissed && !s.AutoMatchBlocked && s.GameId == null)
            .ToListAsync(cancellationToken);

        var matched = 0;

        if (candidates.Count > 0)
        {
            var library = await context.GameTownGames
                .Select(g => new { g.Id, g.Title })
                .ToListAsync(cancellationToken);

            // Built once for the whole run rather than per suggestion: the normalisation is the same
            // work every time, and a LAN library is small enough to hold in memory but large enough
            // that re-normalising it per row is pointless.
            var byKey = library
                .GroupBy(g => SuggestionMatcher.NormalizeTitle(g.Title))
                .Where(group => group.Key.Length > 0)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var row in candidates)
            {
                var key = SuggestionMatcher.NormalizeTitle(row.Name);
                if (key.Length == 0) continue;

                // Exactly one, deliberately. Two library entries sharing a normalised title means the
                // name does not identify a game — two uploads of the same thing, or a remake beside
                // its original — and picking either unattended is a coin flip nobody will audit.
                if (!byKey.TryGetValue(key, out var hits) || hits.Count != 1) continue;

                row.GameId = hits[0].Id;
                row.LinkSource = "auto";
                matched++;
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        return (matched, await PushPendingAsync(cancellationToken));
    }

    /// <summary>
    /// Pushes every matched-but-unbound suggestion: a game push to ensure the catalogue entry exists
    /// and is current, then a direct bind.
    ///
    /// EACH SUGGESTION IS PUSHED AT MOST ONCE — enforced by the <c>RemoteMatchId == null</c> filter
    /// below, not by any other flag. Bindings at the far end are additive, so two suggestions for one
    /// game each get their own bind and both keep it; the game push that precedes a bind is harmless
    /// to repeat for a second suggestion sharing the same game, since it only ever updates that one
    /// entry's title/url/boxArtUrl to the same values.
    ///
    /// Because nothing is re-pushed once bound, a steady state costs one GET per interval and no
    /// writes at all against the crew's system — which matters when it is someone else's service on a
    /// timer.
    /// </summary>
    private async Task<int> PushPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await context.LanSuggestions
            .Include(s => s.Game)
            .Where(s => !s.Dismissed && s.GameId != null && s.RemoteMatchId == null)
            .OrderBy(s => s.RemoteId)
            .ToListAsync(cancellationToken);

        var publicBaseUrl = await settings.GetPublicBaseUrlAsync();
        var bound = 0;

        foreach (var row in pending)
        {
            var gameId = row.GameId!.Value;
            var game = row.Game!;

            var (upserted, upsertReason, _) = await bot.UpsertGameAsync(
                gameId, game.Title, DeepLink(gameId, publicBaseUrl),
                BoxArtAbsoluteUrl(game.BoxArtUrl, publicBaseUrl), cancellationToken);

            if (!upserted)
            {
                // Giving up rather than working through the rest. All three are about the far end
                // rather than about this row, so the remaining calls would fail the same way — and
                // rate-limited in particular means "stop", not "try harder". The rows are left
                // untouched and the next run picks them up.
                if (upsertReason is "rate-limited" or "unreachable" or "not-configured") break;

                continue;
            }

            var (matched, matchReason) = await bot.MatchSuggestionAsync(row.RemoteId, gameId, cancellationToken);

            if (!matched)
            {
                if (matchReason is "rate-limited" or "unreachable" or "not-configured") break;

                continue;
            }

            row.RemoteMatchId = gameId;
            row.PushedAtUtc = DateTime.UtcNow;
            bound++;
        }

        if (bound > 0) await context.SaveChangesAsync(cancellationToken);
        return bound;
    }

    /// <summary>
    /// Links a suggestion to a library entry by hand: pushes the catalogue entry, then binds directly.
    ///
    /// The direct bind is unconditional, so linking a suggestion that already points somewhere else —
    /// the crew's own catalogue entry, or a different GameTown game — succeeds and re-points it. That
    /// is deliberate: this is a human decision the bot has no stronger claim to override.
    ///
    /// The local row is only updated when both calls succeeded. Recording a link we could not push
    /// would make GameTown claim something about the bot that is not true, and the screen would show a
    /// green tick for a Discord link that does not exist.
    /// </summary>
    public async Task<LanLinkResult> LinkAsync(
        long remoteId, Guid gameId, CancellationToken cancellationToken = default)
    {
        var row = await context.LanSuggestions.FirstOrDefaultAsync(s => s.RemoteId == remoteId, cancellationToken);
        if (row is null) return Failed("not-found");

        var game = await context.GameTownGames.FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken);
        if (game is null) return Failed("not-found");

        // A person has chosen, so the matcher stops deciding for this row either way.
        row.GameId = gameId;
        row.LinkSource = "manual";
        row.AutoMatchBlocked = false;

        var publicBaseUrl = await settings.GetPublicBaseUrlAsync();

        var (upserted, upsertReason, _) = await bot.UpsertGameAsync(
            gameId, game.Title, DeepLink(gameId, publicBaseUrl),
            BoxArtAbsoluteUrl(game.BoxArtUrl, publicBaseUrl), cancellationToken);
        if (!upserted) return Failed(upsertReason);

        var (matched, matchReason) = await bot.MatchSuggestionAsync(remoteId, gameId, cancellationToken);
        if (!matched) return Failed(matchReason);

        row.RemoteMatchId = gameId;
        row.PushedAtUtc = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);
        return new LanLinkResult { Ok = true, Reason = "ok" };
    }

    /// <summary>
    /// Undoes a link, clearing this suggestion's own match at the bot without touching the catalogue
    /// entry or any other suggestion bound to it.
    ///
    /// The GameTown-created vs. adopted (LinkSource "remote") distinction that used to gate a
    /// whole-entry delete no longer applies — a per-suggestion unbind is safe in every case.
    /// </summary>
    public async Task<LanLinkResult> UnlinkAsync(long remoteId, CancellationToken cancellationToken = default)
    {
        var row = await context.LanSuggestions.FirstOrDefaultAsync(s => s.RemoteId == remoteId, cancellationToken);
        if (row is null) return Failed("not-found");

        if (row.RemoteMatchId is not null)
        {
            var (ok, reason) = await bot.UnmatchSuggestionAsync(remoteId, cancellationToken);

            // Left exactly as it was. Releasing locally while the bot still reports it bound would
            // leave a Discord link nothing in GameTown admits to.
            if (!ok) return Failed(reason);
        }

        row.GameId = null;
        row.LinkSource = null;
        row.RemoteMatchId = null;
        row.PushedAtUtc = null;

        // Makes the unlink stick. Auto-matching fires on exact normalised titles, so without this the
        // next sync would match this suggestion to the same game and push it again — an unlink that
        // appears to work and undoes itself minutes later.
        row.AutoMatchBlocked = true;

        await context.SaveChangesAsync(cancellationToken);
        return new LanLinkResult { Ok = true, Reason = "ok" };
    }

    /// <summary>
    /// Marks a suggestion as never going to be matched, or restores it.
    ///
    /// Purely local — the bot is not told, because this is GameTown's judgement about its own wishlist
    /// and not a statement about the suggestion. A dismissed row that is already bound keeps its
    /// binding; dismissing is about the queue, not about undoing work.
    /// </summary>
    public async Task<LanLinkResult> DismissAsync(
        long remoteId, bool dismissed, CancellationToken cancellationToken = default)
    {
        var row = await context.LanSuggestions.FirstOrDefaultAsync(s => s.RemoteId == remoteId, cancellationToken);
        if (row is null) return Failed("not-found");

        row.Dismissed = dismissed;
        await context.SaveChangesAsync(cancellationToken);

        return new LanLinkResult { Ok = true, Reason = "ok" };
    }

    /// <summary>Suggestions, filtered for one of the screen's tabs.</summary>
    public async Task<List<LanSuggestionContract>> GetAsync(
        string state, CancellationToken cancellationToken = default)
    {
        var query = context.LanSuggestions.AsNoTracking().Include(s => s.Game).AsQueryable();

        query = state switch
        {
            "unmatched" => query.Where(s => s.GameId == null && !s.Dismissed),
            "matched" => query.Where(s => s.GameId != null && !s.Dismissed),
            "dismissed" => query.Where(s => s.Dismissed),
            _ => query,
        };

        var rows = await query
            .OrderByDescending(s => s.RemoteId)
            .ToListAsync(cancellationToken);

        var publicBaseUrl = await settings.GetPublicBaseUrlAsync();

        return [.. rows.Select(row => ToContract(row, publicBaseUrl))];
    }

    /// <summary>
    /// One row, on the wire. Shared by the tab listing and the ranked wishlist so the two cannot
    /// drift — <c>IsBound</c> is easy to derive slightly differently in a second place and impossible
    /// to notice when it is.
    /// </summary>
    private static LanSuggestionContract ToContract(LanSuggestion row, string? publicBaseUrl)
        => new()
        {
            RemoteId = row.RemoteId,
            Name = row.Name,
            LanEventName = row.LanEventName,
            Played = row.Played,
            GameId = row.GameId,
            GameTitle = row.Game?.Title,
            LinkSource = row.LinkSource,
            // "The bot points at THIS game", not "the bot points at something". A suggestion the crew
            // bound to their own catalogue entry is bound, and not to anything GameTown can link to.
            IsBound = row.RemoteMatchId is not null && row.RemoteMatchId == row.GameId,
            Dismissed = row.Dismissed,
            DeepLink = DeepLink(row.GameId, publicBaseUrl),
            FirstSeenUtc = row.FirstSeenUtc,
        };

    /// <summary>
    /// The deep link a matched suggestion should point at — both what the screen shows an operator
    /// and, since 1.4.0, the <c>url</c> pushed to the bot's catalogue entry for that game.
    /// </summary>
    private static string? DeepLink(Guid? gameId, string? publicBaseUrl)
        => publicBaseUrl is not null && gameId is not null
            ? $"{publicBaseUrl}/game/{gameId.Value}"
            : null;

    /// <summary>
    /// The absolute cover-art URL pushed to the bot, built from the locally re-hosted
    /// "/media/{guid}.ext" path — see <c>MediaStore</c>. Discord fetches embed images from the
    /// internet, so this is null (and therefore omitted, clearing any previously-pushed cover) unless
    /// both a public address is configured and the game has box art.
    /// </summary>
    private static string? BoxArtAbsoluteUrl(string? relativeBoxArtUrl, string? publicBaseUrl)
        => publicBaseUrl is not null && !string.IsNullOrEmpty(relativeBoxArtUrl)
            ? $"{publicBaseUrl}{relativeBoxArtUrl}"
            : null;

    /// <summary>
    /// Library entries offered as a match for one suggestion, best first.
    ///
    /// Ranking is advisory — see <see cref="SuggestionMatcher.Rank"/>. <c>AlreadyLinkedFrom</c> is
    /// informational too, not a warning: choosing a game another suggestion already points at is
    /// fine, and the note exists so a person can see the game is in play rather than wonder.
    /// </summary>
    public async Task<List<LanCandidateContract>> GetCandidatesAsync(
        long remoteId, CancellationToken cancellationToken = default)
    {
        var row = await context.LanSuggestions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.RemoteId == remoteId, cancellationToken);

        if (row is null) return [];

        var library = await context.GameTownGames
            .AsNoTracking()
            .Select(g => new LibraryEntry(g.Id, g.Title, g.BoxArtUrl))
            .ToListAsync(cancellationToken);

        // Grouped, because a game can be linked from several suggestions at once.
        var linkedFrom = await LinkedFromAsync(cancellationToken, excluding: remoteId);

        return [.. SuggestionMatcher
            .Rank(row.Name, library, game => game.Title)
            .Select(match => ToCandidate(match, linkedFrom, remoteId))];
    }

    /// <summary>
    /// The whole wishlist, ranked and banded, in one pass.
    ///
    /// WHY THIS IS NOT <see cref="GetCandidatesAsync"/> IN A LOOP. That method reads the entire
    /// library per call, so ranking a 78-row queue that way is 78 full library reads and 78 round
    /// trips. Here the library is read once and every suggestion is ranked against the same list.
    ///
    /// More importantly it is what lets the screen SORT by how much work a row is. Until every row
    /// carries a band, the fourteen rows with an obvious answer and the thirty-two with none look
    /// identical, and a person has to open each one to find out which kind it is — which is the
    /// actual reason the old screen did not scale.
    ///
    /// Candidates are dropped from weak rows before they go on the wire. The screen does not render
    /// them, and a list of ten wrong answers per row is most of the payload.
    /// </summary>
    /// <param name="query">Free text over the suggestion's own name. Not over candidate titles —
    /// this narrows the queue, it does not search the library.</param>
    /// <param name="lanEvent">Exact event name, as <c>GetEventsAsync</c> reports them.</param>
    /// <param name="confidence">"strong", "ambiguous" or "weak" to show one section only.</param>
    public async Task<LanRankedQueueContract> GetRankedAsync(
        string? query = null,
        string? lanEvent = null,
        string? confidence = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        // Same predicate as the "unmatched" tab, deliberately. Deriving it independently is what made
        // the re-link banner and the re-link screen disagree.
        var rows = await context.LanSuggestions.AsNoTracking()
            .Include(s => s.Game)
            .Where(s => s.GameId == null && !s.Dismissed)
            .OrderByDescending(s => s.RemoteId)
            .ToListAsync(cancellationToken);

        var library = await context.GameTownGames.AsNoTracking()
            .Select(g => new LibraryEntry(g.Id, g.Title, g.BoxArtUrl))
            .ToListAsync(cancellationToken);

        var linkedFrom = await LinkedFromAsync(cancellationToken);
        var publicBaseUrl = await settings.GetPublicBaseUrlAsync();

        var ranked = new List<LanRankedSuggestionContract>(rows.Count);

        foreach (var row in rows)
        {
            var matches = SuggestionMatcher.Rank(row.Name, library, entry => entry.Title);
            var band = MatchConfidenceBands.Band([.. matches.Select(m => m.Score)]);

            ranked.Add(new LanRankedSuggestionContract
            {
                Suggestion = ToContract(row, publicBaseUrl),
                // Nothing at all for a weak row, and only the candidates worth reading for the rest.
                // Both trims are the same idea at different scales — see MatchConfidenceBands.
                Candidates = band == MatchConfidence.Weak
                    ? []
                    : [.. MatchConfidenceBands
                        .Nearest(matches, m => m.Score)
                        .Select(m => ToCandidate(m, linkedFrom, row.RemoteId))],
                Confidence = MatchConfidenceBands.Name(band),
                // The one rule that is not about string similarity: a row whose link a contributor
                // removed must never arrive pre-ticked, or a bulk link would undo their decision.
                Preselect = band == MatchConfidence.Strong && !row.AutoMatchBlocked,
                AutoMatchBlocked = row.AutoMatchBlocked,
            });
        }

        // Counted over the whole queue, before any filter: these drive the section headings, and a
        // heading that changed as you typed in the filter box would be reporting on the filter rather
        // than on the work outstanding.
        var result = new LanRankedQueueContract
        {
            StrongCount = ranked.Count(r => r.Confidence == "strong"),
            AmbiguousCount = ranked.Count(r => r.Confidence == "ambiguous"),
            WeakCount = ranked.Count(r => r.Confidence == "weak"),
        };

        // ORDERED BY BAND, BEFORE PAGING, and this is load-bearing rather than cosmetic. The screen
        // draws the queue as three sections and offers a bulk action over each; if a page were a
        // slice of the whole queue in arrival order, the "Likely matches" section would hold whichever
        // handful of strong rows happened to fall on that page — one of fourteen, in the first run of
        // this — and "Link selected" would silently mean "link the ones you can currently see".
        // Sorting by band makes a page a meaningful unit of work: the certain ones first, together.
        //
        // Arrival order is kept WITHIN a band, so the queue is still newest-first where that is all
        // there is to go on.
        IEnumerable<LanRankedSuggestionContract> filtered = ranked
            .OrderBy(r => r.Confidence switch { "strong" => 0, "ambiguous" => 1, _ => 2 })
            .ThenByDescending(r => r.Suggestion.RemoteId);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var wanted = query.Trim();
            filtered = filtered.Where(r =>
                r.Suggestion.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(lanEvent))
        {
            filtered = filtered.Where(r =>
                string.Equals(r.Suggestion.LanEventName, lanEvent, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(confidence))
        {
            filtered = filtered.Where(r => r.Confidence == confidence);
        }

        var matching = filtered.ToList();
        result.TotalMatching = matching.Count;

        result.Suggestions = [.. matching
            .Skip(Math.Max(page - 1, 0) * Math.Max(pageSize, 1))
            .Take(Math.Max(pageSize, 1))];

        return result;
    }

    /// <summary>What the library looks like to the matcher: a title to rank and a cover to show.</summary>
    private sealed record LibraryEntry(Guid Id, string Title, string? BoxArtUrl);

    /// <summary>
    /// Which games are already linked from some OTHER suggestion, by name.
    ///
    /// Informational, never a warning — bindings at the bot are additive, so choosing a game already
    /// in play costs nothing. Read once per request rather than once per suggestion.
    /// </summary>
    private async Task<Dictionary<Guid, List<string>>> LinkedFromAsync(
        CancellationToken cancellationToken, long? excluding = null)
    {
        var rows = await context.LanSuggestions.AsNoTracking()
            .Where(s => s.RemoteMatchId != null)
            .Select(s => new { MatchId = s.RemoteMatchId!.Value, s.Name, s.RemoteId })
            .ToListAsync(cancellationToken);

        return rows
            .Where(s => excluding is null || s.RemoteId != excluding)
            .GroupBy(s => s.MatchId)
            .ToDictionary(group => group.Key, group => group.Select(s => s.Name).ToList());
    }

    private static LanCandidateContract ToCandidate(
        RankedMatch<LibraryEntry> match, Dictionary<Guid, List<string>> linkedFrom, long selfRemoteId)
        => new()
        {
            GameId = match.Item.Id,
            Title = match.Item.Title,
            BoxArtUrl = match.Item.BoxArtUrl,
            Score = Math.Round(match.Score, 3),
            IsExact = match.IsExact,
            // The row's own name would otherwise show up as "also linked from" itself.
            AlreadyLinkedFrom = linkedFrom.GetValueOrDefault(match.Item.Id) ?? [],
        };

    /// <summary>
    /// Links several suggestions in one request, stopping early on a reason that will not improve.
    ///
    /// The pacing lives here rather than in the browser because these are outbound calls to a
    /// rate-limited bot and a page can be closed halfway through a loop. Each row still goes through
    /// <see cref="LinkAsync"/>, so every rule about linking — including the unconditional re-point —
    /// holds exactly as it does for a single link.
    /// </summary>
    public async Task<LanBulkResult> LinkManyAsync(
        IEnumerable<LanLinkRequest> links, CancellationToken cancellationToken = default)
    {
        var result = new LanBulkResult();

        foreach (var link in links)
        {
            var name = await context.LanSuggestions.AsNoTracking()
                .Where(s => s.RemoteId == link.RemoteId)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

            var outcome = await LinkAsync(link.RemoteId, link.GameId, cancellationToken);

            result.Results.Add(new LanBulkEntry
            {
                RemoteId = link.RemoteId,
                Name = name,
                Ok = outcome.Ok,
                Reason = outcome.Reason,
            });

            if (outcome.Ok)
            {
                result.Succeeded++;
                continue;
            }

            result.Failed++;

            // "not-found" is about this row only — the suggestion or game went away — so the rest of
            // the batch is still worth trying. Everything else is about the bot or its credentials
            // and will answer the same way thirty more times, each one after a timeout.
            if (outcome.Reason is not "not-found")
            {
                result.StoppedBecause = outcome.Reason;
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Sets several suggestions aside, or puts them back.
    ///
    /// Local only and therefore never stops early: there is no outbound call to fail, and a row that
    /// has gone missing is not a reason to abandon the rest.
    /// </summary>
    public async Task<LanBulkResult> DismissManyAsync(
        IEnumerable<long> remoteIds, bool dismissed, CancellationToken cancellationToken = default)
    {
        var wanted = remoteIds.Distinct().ToList();

        var rows = await context.LanSuggestions
            .Where(s => wanted.Contains(s.RemoteId))
            .ToListAsync(cancellationToken);

        var result = new LanBulkResult();

        foreach (var remoteId in wanted)
        {
            var row = rows.FirstOrDefault(r => r.RemoteId == remoteId);

            if (row is null)
            {
                result.Failed++;
                result.Results.Add(new LanBulkEntry { RemoteId = remoteId, Ok = false, Reason = "not-found" });
                continue;
            }

            row.Dismissed = dismissed;

            result.Succeeded++;
            result.Results.Add(new LanBulkEntry
            {
                RemoteId = remoteId,
                Name = row.Name,
                Ok = true,
                Reason = "ok",
            });
        }

        // One save for the batch. Setting thirty rows aside is one decision, and a partial result
        // would leave the queue in a state nobody chose.
        await context.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// How many suggestions are waiting on a human.
    ///
    /// Must use the same predicate as the "unmatched" tab. Deriving it independently is what made the
    /// re-link banner and the re-link screen disagree — see <c>MetadataRelinkService.CountCandidatesAsync</c>.
    /// </summary>
    public Task<int> CountUnmatchedAsync(CancellationToken cancellationToken = default)
        => context.LanSuggestions.AsNoTracking()
            .CountAsync(s => s.GameId == null && !s.Dismissed, cancellationToken);

    /// <summary>
    /// Every suggestion in every state — the honest test for whether this install uses the LAN bot.
    ///
    /// A COUNT, not a list. The sidebar link used to answer this by pulling the entire suggestion
    /// list and reading its Count, which on an install with an empty wishlist meant downloading every
    /// suggestion on every page load to decide one boolean.
    /// </summary>
    public Task<int> CountAllAsync(CancellationToken cancellationToken = default)
        => context.LanSuggestions.AsNoTracking().CountAsync(cancellationToken);

    /// <summary>
    /// LAN events with at least one library game suggested for them, for the shelf chips.
    ///
    /// Counts distinct GAMES, not suggestions: two people asking for the same game is one entry on the
    /// shelf, and a chip promising four games that opens onto three is the kind of small wrongness
    /// that makes a screen feel untrustworthy.
    /// </summary>
    public async Task<List<LanEventContract>> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await context.LanSuggestions
            .AsNoTracking()
            .Where(s => s.GameId != null)
            .Select(s => new { s.LanEventName, s.GameId })
            .ToListAsync(cancellationToken);

        return [.. rows
            .GroupBy(r => r.LanEventName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LanEventContract
            {
                Name = group.Key,
                GameCount = group.Select(r => r.GameId).Distinct().Count(),
            })
            .OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The settings screen's status panel.</summary>
    public async Task<LanSyncStatusContract> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = state.Snapshot();

        return new LanSyncStatusContract
        {
            Configured = await bot.IsConfiguredAsync(),
            Running = snapshot.Running,
            LastRunUtc = snapshot.LastRunUtc,
            LastReason = snapshot.LastReason,
            LastSeen = snapshot.Seen,
            LastMatched = snapshot.Matched,
            LastBound = snapshot.Bound,
            IntervalMinutes = await settings.GetLanBotSyncIntervalMinutesAsync(),
        };
    }

    private static LanLinkResult Failed(string reason) => new() { Ok = false, Reason = reason };
}
