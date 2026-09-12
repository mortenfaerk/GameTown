namespace GameTown.Contracts.Lan;

/// <summary>
/// One game suggestion from the LAN Discord bot, as GameTown holds it.
///
/// Two different claims about the same row, and keeping them apart is the whole point of this
/// contract: <see cref="GameId"/> is what GAMETOWN worked out, <see cref="IsBound"/> is what the BOT
/// actually has. They come apart whenever a push has not happened yet or failed.
/// </summary>
public class LanSuggestionContract
{
    /// <summary>The bot's own id. The address for every action on this row.</summary>
    public long RemoteId { get; set; }

    /// <summary>
    /// As a player typed it in Discord. UNTRUSTED — render as text, never as markup. See
    /// SECURITY-NOTES.md risk 10.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Also player-supplied, and untrusted on the same terms as <see cref="Name"/>.</summary>
    public string LanEventName { get; set; } = string.Empty;

    public bool Played { get; set; }

    /// <summary>The library entry this suggestion is, as far as GameTown is concerned.</summary>
    public Guid? GameId { get; set; }

    public string? GameTitle { get; set; }

    /// <summary>"auto", "manual" or "remote" — where the match came from. Null when unmatched.</summary>
    public string? LinkSource { get; set; }

    /// <summary>
    /// Whether the bot points at <see cref="GameId"/> — THIS game — for this suggestion.
    ///
    /// Deliberately not "bound to anything": a suggestion the crew linked to their own catalogue
    /// entry inside the bot is bound, but not to a GameTown game, and reporting that as a working
    /// link would be a green tick for something Discord does not have.
    /// </summary>
    public bool IsBound { get; set; }

    /// <summary>"Nobody is going to upload this" — keeps the wishlist a list of work.</summary>
    public bool Dismissed { get; set; }

    /// <summary>
    /// The deep link the bot should use for this suggestion, when a public address is configured and
    /// the row is matched. Shown so an operator can hand the exact URL to whoever runs the bot — the
    /// Catalogue API has no field to receive it.
    /// </summary>
    public string? DeepLink { get; set; }

    public DateTime FirstSeenUtc { get; set; }
}

/// <summary>
/// A library entry offered as a match for a suggestion, ranked.
///
/// <see cref="IsExact"/> is the same test the automatic matcher uses; anything else is a suggestion
/// to a human and is never acted on unattended.
/// </summary>
public class LanCandidateContract
{
    public Guid GameId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? BoxArtUrl { get; set; }

    /// <summary>0..1. Ordering only — do not read a threshold into it.</summary>
    public double Score { get; set; }

    /// <summary>Whether the normalised titles are equal, which is what auto-matching requires.</summary>
    public bool IsExact { get; set; }

    /// <summary>
    /// Other suggestions already linked to this game. Empty is the common case.
    ///
    /// Informational, NOT a warning: bindings at the far end are additive, so linking a game that is
    /// already in play costs nothing and breaks nothing. The note exists so a person can see the game
    /// is already spoken for rather than wonder whether they are duplicating someone else's work.
    /// </summary>
    public List<string> AlreadyLinkedFrom { get; set; } = [];
}

/// <summary>
/// One wishlist row with its candidates and a confidence band already worked out.
///
/// Exists so the whole queue can be ranked in ONE request instead of one per row on demand. That is
/// not only a round-trip saving: until every row carries a band, the screen cannot tell the fourteen
/// rows with an obvious answer from the thirty-two with none, so it has to render them identically
/// and a person has to open each one to find out which kind it is.
/// </summary>
public class LanRankedSuggestionContract
{
    public LanSuggestionContract Suggestion { get; set; } = new();

    /// <summary>
    /// Best first, empty when <see cref="Confidence"/> is "weak" — there is no point spending bytes
    /// on ten candidates the screen has already decided not to show.
    /// </summary>
    public List<LanCandidateContract> Candidates { get; set; } = [];

    /// <summary>
    /// "strong", "ambiguous" or "weak". See <c>MatchConfidenceBands</c> for what separates them and
    /// for the measurements that set the thresholds.
    /// </summary>
    public string Confidence { get; set; } = "weak";

    /// <summary>
    /// Whether the screen should arrive with this row's top candidate already ticked.
    ///
    /// Deliberately its own field rather than <c>Confidence == "strong"</c> computed in the page: a
    /// row whose link a contributor has already removed carries <c>AutoMatchBlocked</c>, and
    /// re-proposing it pre-ticked would let a bulk "Link selected" quietly undo the very decision
    /// that flag exists to record. One place to get that right, on the server, next to the flag.
    /// </summary>
    public bool Preselect { get; set; }

    /// <summary>
    /// A human has already decided about this row once and removed its link.
    ///
    /// Carried so the screen can say why a confident-looking match is not ticked. Without it the row
    /// reads as an oversight and somebody helpfully ticks it.
    /// </summary>
    public bool AutoMatchBlocked { get; set; }
}

/// <summary>
/// The whole wishlist, banded, plus the counts the screen puts on its section headings.
///
/// The counts are returned rather than derived from the lists because the lists are paged.
/// </summary>
public class LanRankedQueueContract
{
    public List<LanRankedSuggestionContract> Suggestions { get; set; } = [];

    public int StrongCount { get; set; }
    public int AmbiguousCount { get; set; }
    public int WeakCount { get; set; }

    /// <summary>Matching every filter, before paging. What "showing 25 of 78" is counting.</summary>
    public int TotalMatching { get; set; }
}

public class LanLinkRequest
{
    public long RemoteId { get; set; }
    public Guid GameId { get; set; }
}

/// <summary>
/// Several links in one request, applied in order.
///
/// One request rather than one per row because these are outbound calls to a rate-limited bot, and
/// sequencing them in the browser means the pacing lives in a page that can be closed halfway
/// through. Each pair is still pushed at most once — the bulk path changes how many links are asked
/// for, never how often one is pushed.
/// </summary>
public class LanBulkLinkRequest
{
    public List<LanLinkRequest> Links { get; set; } = [];
}

public class LanBulkDismissRequest
{
    public List<long> RemoteIds { get; set; } = [];
    public bool Dismissed { get; set; }
}

/// <summary>What a bulk operation did, per row.</summary>
public class LanBulkResult
{
    public List<LanBulkEntry> Results { get; set; } = [];

    /// <summary>Rows where <see cref="LanLinkResult.Ok"/> came back true.</summary>
    public int Succeeded { get; set; }

    public int Failed { get; set; }

    /// <summary>
    /// The reason the run stopped early, or null if every row was attempted.
    ///
    /// A bulk run abandons the rest on the first reason that will not improve by trying again —
    /// "not-configured", "rejected", "rate-limited", "unreachable" — because thirty more failures
    /// against an unreachable bot is thirty timeouts and the same answer.
    /// </summary>
    public string? StoppedBecause { get; set; }
}

public class LanBulkEntry
{
    public long RemoteId { get; set; }

    /// <summary>The suggestion's name, so the caller can report without re-reading its own list.</summary>
    public string Name { get; set; } = string.Empty;

    public bool Ok { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class LanRemoteIdRequest
{
    public long RemoteId { get; set; }
}

public class LanDismissRequest
{
    public long RemoteId { get; set; }
    public bool Dismissed { get; set; }
}

/// <summary>
/// The outcome of a link, unlink or dismiss.
///
/// <see cref="Reason"/> is a fixed code, never exception text — these operations make an outbound
/// request to an address an admin configured, and raw exception text would disclose it.
/// </summary>
public class LanLinkResult
{
    public bool Ok { get; set; }

    /// <summary>
    /// "ok", "game-not-found", "not-configured", "rejected", "rate-limited", "unreachable" or
    /// "not-found".
    ///
    /// "game-not-found" is the bot's 400 on the direct-bind call — the catalogue entry has to exist
    /// before a suggestion can be pointed at it. GameTown always pushes the game first, so this should
    /// only ever surface a genuine bug rather than a normal outcome.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// How much is waiting on a human, without listing it. Drives the sidebar link, which appears and
/// disappears on this one number — see <c>RelinkStatusContract</c> for the same reasoning.
/// </summary>
public class LanCountContract
{
    public int Unmatched { get; set; }

    /// <summary>
    /// Every suggestion in every state, which is the honest test for "does this install use the LAN
    /// bot at all" — suggestions exist only once a sync has run.
    ///
    /// It is here because the sidebar link needs both numbers and used to get the second by pulling
    /// the ENTIRE suggestion list and taking its Count. On an install whose wishlist is empty — the
    /// healthy steady state — that downloaded every suggestion on every page load of the app, to
    /// decide one boolean.
    /// </summary>
    public int Total { get; set; }
}

/// <summary>A LAN event and how many library games were suggested for it. Feeds the shelf chips.</summary>
public class LanEventContract
{
    /// <summary>Player-supplied, and untrusted on the same terms as <see cref="LanSuggestionContract.Name"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Games in the library suggested for this event — not total suggestions.</summary>
    public int GameCount { get; set; }
}

/// <summary>
/// What the last sync did. Held in memory, so it resets on restart — that is honest rather than
/// lossy: the interesting question is whether polling is working NOW, and a persisted "ok" from
/// before a crash answers it wrongly.
/// </summary>
public class LanSyncStatusContract
{
    public bool Configured { get; set; }
    public bool Running { get; set; }
    public DateTime? LastRunUtc { get; set; }

    /// <summary>
    /// Fixed reason code from the last run, or null if it has not run yet.
    ///
    /// "busy" is the one that is not about the bot: a sync was already in flight, so this caller did
    /// nothing. It is a normal outcome now that contributors can refresh the wishlist while the
    /// background worker is also polling, and the screen treats it as such rather than as a failure.
    /// </summary>
    public string? LastReason { get; set; }

    public int LastSeen { get; set; }
    public int LastMatched { get; set; }
    public int LastBound { get; set; }

    /// <summary>Minutes between polls, as configured. <c>0</c> means polling is off.</summary>
    public int IntervalMinutes { get; set; }
}
