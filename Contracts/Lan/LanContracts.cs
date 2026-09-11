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
    /// link would be a green tick for something Discord does not have. See
    /// <see cref="BoundElsewhere"/> for that case.
    /// </summary>
    public bool IsBound { get; set; }

    /// <summary>
    /// The bot has this suggestion pointing at something that is not <see cref="GameId"/> — usually a
    /// catalogue entry the crew made by hand.
    ///
    /// It matters because it is not fixable from here: the bot binds only suggestions that are
    /// currently UNBOUND and will not move an existing binding, so GameTown can record what the game
    /// is and can do nothing about the link. The screen has to say that rather than show a link as
    /// pending forever.
    /// </summary>
    public bool BoundElsewhere { get; set; }

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

public class LanLinkRequest
{
    public long RemoteId { get; set; }
    public Guid GameId { get; set; }
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
    /// "ok", "bound-elsewhere", "not-configured", "rejected", "rate-limited", "unreachable" or
    /// "not-found".
    ///
    /// "bound-elsewhere" comes back with <see cref="Ok"/> TRUE and is not a failure: GameTown recorded
    /// the game, but the bot already had that suggestion pointing at something else and does not move
    /// an existing binding. The caller has to say so — it is the one success that puts no link in
    /// Discord.
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

    /// <summary>Fixed reason code from the last run, or null if it has not run yet.</summary>
    public string? LastReason { get; set; }

    public int LastSeen { get; set; }
    public int LastMatched { get; set; }
    public int LastBound { get; set; }

    /// <summary>Minutes between polls, as configured. <c>0</c> means polling is off.</summary>
    public int IntervalMinutes { get; set; }
}
