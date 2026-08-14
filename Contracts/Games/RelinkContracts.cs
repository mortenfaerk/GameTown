namespace GameTown.Contracts.Games;

/// <summary>
/// A library entry still pointing at a retired provider's metadata.
///
/// Note what is NOT here: nothing about box art, tags or instructions. Re-linking cannot affect them,
/// so the screen has no reason to show them and the contract has no reason to carry them.
/// </summary>
public class RelinkCandidateContract
{
    public Guid GameId { get; set; }

    /// <summary>The library title — what the contributor typed on upload.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The name on the metadata record, which is what the search actually runs against. Shown next to
    /// the title because they often differ, and the difference explains a surprising match.
    /// </summary>
    public string MetadataName { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;
    public DateTime? Released { get; set; }
}

/// <summary>
/// How much there is to re-link, without listing it.
///
/// Exists for the callers that only need to know whether the number is zero — the sidebar link and
/// the settings banner both appear or disappear on that one fact, and fetching every candidate row to
/// answer it would grow with the library while the answer stayed one integer.
/// </summary>
public class RelinkStatusContract
{
    public int Candidates { get; set; }
}

public class RelinkProposeRequest
{
    public List<Guid> GameIds { get; set; } = [];
}

/// <summary>
/// A proposed pairing. Nothing has been written; this is what the admin is being asked about.
/// </summary>
public class RelinkProposalContract
{
    public Guid GameId { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Whether the screen should pre-tick this row: an exact name match whose release year also
    /// agrees. Everything else arrives unticked and has to be chosen deliberately — the failure being
    /// guarded is an admin skimming a list of already-ticked boxes and confirming a remake in place of
    /// its original.
    /// </summary>
    public bool IsConfident { get; set; }

    /// <summary>"ok", "no-match", "not-configured", "rejected", "rate-limited" or "unreachable".</summary>
    public string Reason { get; set; } = string.Empty;

    public int? MatchExternalId { get; set; }
    public string? MatchName { get; set; }
    public DateTime? MatchReleased { get; set; }
    public string? MatchThumbnailUrl { get; set; }
}

public class RelinkPairing
{
    public Guid GameId { get; set; }

    /// <summary>The provider's id for the chosen match, not a local metadata id.</summary>
    public int ExternalId { get; set; }
}

public class RelinkApplyRequest
{
    public List<RelinkPairing> Pairings { get; set; } = [];
}

/// <summary>
/// Per-game outcomes rather than one verdict for the batch: these are independent games, and an admin
/// who confirmed twenty needs to know which of them did not take.
/// </summary>
public class RelinkApplyResult
{
    public List<Guid> Applied { get; set; } = [];
    public List<RelinkFailure> Failed { get; set; } = [];
}

public class RelinkFailure
{
    public Guid GameId { get; set; }
    public string Reason { get; set; } = string.Empty;
}
