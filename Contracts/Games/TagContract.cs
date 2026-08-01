namespace GameTown.Contracts.Games;

/// <summary>
/// A manual tag. These describe how a game gets played — split screen, LAN, co-op — which is the
/// question the shelf actually gets asked and the one thing RAWG's genres cannot answer.
/// </summary>
public class TagContract
{
    public Guid Id { get; set; }

    /// <summary>As typed and as displayed, e.g. "Split screen".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The tag's identity: lowercase, non-alphanumerics collapsed to '-'. This is what filters travel
    /// as, so a link to <c>/?tags=split-screen</c> survives the tag being renamed for display.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Offered as a one-click button in the tag editor.</summary>
    public bool IsQuickAdd { get; set; }

    /// <summary>
    /// How many games carry this tag. Populated by the tag list endpoint and left at zero on the tags
    /// embedded in a game — counting there would mean an extra query per game per page.
    /// </summary>
    public int GameCount { get; set; }
}

/// <summary>
/// Replaces a game's entire tag set.
///
/// Whole-set rather than add/remove pairs: the editor is a chip list, so this is the shape the UI
/// already has, and it makes the request idempotent — a retry after a dropped response cannot leave
/// the game with a tag applied twice or removed twice.
///
/// Names are resolved to existing tags by slug, so sending "LAN", "lan" or " Lan " all land on the
/// one tag. Anything unrecognised is created.
/// </summary>
public class SetGameTagsRequest
{
    public List<string> Names { get; set; } = [];
}
