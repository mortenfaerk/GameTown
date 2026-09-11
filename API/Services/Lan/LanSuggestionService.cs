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
/// HOW THE BOT ACTUALLY BINDS, verified against the live one rather than inferred from its API — all
/// three of these shape the code below and none is obvious from the OpenAPI document:
///
///  1. A PUT replaces the catalogue entry's TITLE, and the entry holds exactly one.
///  2. It then binds every suggestion whose name equals that title AND IS CURRENTLY UNBOUND.
///  3. It never unbinds and never re-points anything. Only DELETE removes bindings.
///
/// So several suggestions can share one game (2), a suggestion the crew already bound cannot be
/// claimed by GameTown at all (3), and re-pushing is pointless as well as harmful — it would leave
/// the catalogue title flip-flopping between two names forever (1).
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
    /// Pushes every matched-but-unbound suggestion, one PUT each.
    ///
    /// EACH SUGGESTION IS PUSHED AT MOST ONCE, and that is the rule the bot's one-title-per-entry
    /// limit actually demands. Bindings at the far end are additive, so two suggestions for one game
    /// each get their own and both keep it; what a repeat push would change is the catalogue entry's
    /// TITLE, which would then flip between the two names on every sync forever — a Discord entry
    /// whose label keeps changing, logging nothing, visible only to someone who looked twice.
    ///
    /// Because nothing is re-pushed, a steady state costs one GET per interval and no writes at all
    /// against the crew's system — which matters when it is someone else's service on a timer.
    /// </summary>
    private async Task<int> PushPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await context.LanSuggestions
            .Where(s => !s.Dismissed && s.GameId != null && s.RemoteMatchId == null)
            .OrderBy(s => s.RemoteId)
            .ToListAsync(cancellationToken);

        var bound = 0;

        foreach (var row in pending)
        {
            var gameId = row.GameId!.Value;
            var (ok, reason, boundByPut) = await bot.UpsertGameAsync(gameId, row.Name, cancellationToken);

            if (ok && boundByPut == 0)
            {
                // The entry was written but this suggestion was not bound to it — the bot only binds
                // suggestions that are currently unbound, and something bound this one since the pull
                // at the start of this run. Leave the row alone; the next reconcile reports the truth.
                continue;
            }

            if (!ok)
            {
                // Giving up rather than working through the rest. All three are about the far end
                // rather than about this row, so the remaining calls would fail the same way — and
                // rate-limited in particular means "stop", not "try harder". The rows are left
                // untouched and the next run picks them up.
                if (reason is "rate-limited" or "unreachable" or "not-configured") break;

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
    /// Links a suggestion to a library entry by hand, pushing it to the bot.
    ///
    /// Linking a game another suggestion already points at is fine and needs no confirmation —
    /// bindings at the far end are additive, so the other one keeps working. The only thing that
    /// changes is which name the bot's catalogue entry displays.
    ///
    /// The local row is only updated when the push succeeded. Recording a link we could not push would
    /// make GameTown claim something about the bot that is not true, and the screen would show a green
    /// tick for a Discord link that does not exist.
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

        // The bot will not MOVE a binding — verified against the live one: a PUT binds only the
        // suggestions that are currently unbound, and answers suggestionsBound=0 otherwise. So when
        // this suggestion already points somewhere else there is nothing to send, and claiming
        // otherwise would show a link in GameTown that Discord does not have.
        //
        // The local match is still recorded: GameTown genuinely knows which game this is, and the
        // badge, the shelf and the wishlist all read that rather than the binding.
        if (row.RemoteMatchId is { } existing && existing != gameId)
        {
            await context.SaveChangesAsync(cancellationToken);
            return new LanLinkResult { Ok = true, Reason = "bound-elsewhere" };
        }

        var (ok, reason, bound) = await bot.UpsertGameAsync(gameId, row.Name, cancellationToken);
        if (!ok) return Failed(reason);

        // suggestionsBound is the bot's own report of what the PUT achieved. Zero means it bound
        // nothing — someone bound this suggestion between the last sync and now — so the entry exists
        // but this suggestion is not on it, and saying so is the only honest answer.
        if (bound == 0 && row.RemoteMatchId != gameId)
        {
            await context.SaveChangesAsync(cancellationToken);
            return new LanLinkResult { Ok = true, Reason = "bound-elsewhere" };
        }

        row.RemoteMatchId = gameId;
        row.PushedAtUtc = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);
        return new LanLinkResult { Ok = true, Reason = "ok" };
    }

    /// <summary>
    /// Undoes a link, deleting the catalogue entry when GameTown was the one that created it.
    ///
    /// <c>PushedAtUtc</c> is what makes that safe. The catalogue also holds entries the crew made by
    /// hand inside the bot, and an unlink here must never delete one of those — so a row we merely
    /// adopted (LinkSource "remote") releases locally and leaves the bot alone.
    /// </summary>
    public async Task<LanLinkResult> UnlinkAsync(long remoteId, CancellationToken cancellationToken = default)
    {
        var row = await context.LanSuggestions.FirstOrDefaultAsync(s => s.RemoteId == remoteId, cancellationToken);
        if (row is null) return Failed("not-found");

        if (row.RemoteMatchId is { } matchId && row.PushedAtUtc is not null)
        {
            var (ok, reason) = await bot.DeleteGameAsync(matchId, cancellationToken);

            // Left exactly as it was. Releasing locally while the entry survives at the far end would
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

        return [.. rows.Select(row => new LanSuggestionContract
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
            BoundElsewhere = row.RemoteMatchId is not null && row.RemoteMatchId != row.GameId,
            Dismissed = row.Dismissed,
            DeepLink = publicBaseUrl is not null && row.GameId is not null
                ? $"{publicBaseUrl}/game/{row.GameId.Value}"
                : null,
            FirstSeenUtc = row.FirstSeenUtc,
        })];
    }

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
            .Select(g => new { g.Id, g.Title, g.BoxArtUrl })
            .ToListAsync(cancellationToken);

        // Grouped, because a game can be linked from several suggestions at once.
        var linkedRows = await context.LanSuggestions
            .AsNoTracking()
            .Where(s => s.RemoteMatchId != null && s.RemoteId != remoteId)
            .Select(s => new { MatchId = s.RemoteMatchId!.Value, s.Name })
            .ToListAsync(cancellationToken);

        var linkedFrom = linkedRows
            .GroupBy(s => s.MatchId)
            .ToDictionary(group => group.Key, group => group.Select(s => s.Name).ToList());

        return [.. SuggestionMatcher
            .Rank(row.Name, library, game => game.Title)
            .Select(match => new LanCandidateContract
            {
                GameId = match.Item.Id,
                Title = match.Item.Title,
                BoxArtUrl = match.Item.BoxArtUrl,
                Score = Math.Round(match.Score, 3),
                IsExact = match.IsExact,
                AlreadyLinkedFrom = linkedFrom.GetValueOrDefault(match.Item.Id) ?? [],
            })];
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
