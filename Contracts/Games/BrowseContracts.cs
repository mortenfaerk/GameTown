namespace GameTown.Contracts.Games;

/// <summary>
/// One batch of the library shelf, as served by <c>GET /GTGames/browse</c>.
///
/// Cursor-paged rather than page-numbered because the shelf scrolls endlessly: an offset page shifts
/// by one whenever a game is added or removed ahead of it, and on an appended list that shows up as a
/// tile repeated or a tile never shown. A cursor names the last row seen, so what comes next does not
/// depend on how many rows sit in front of it.
/// </summary>
public class BrowsePageContract
{
    public List<GameContract> Items { get; set; } = [];

    /// <summary>
    /// Opaque — pass back as <c>?after=</c> for the next batch. Null means this was the last one.
    /// </summary>
    public string? Next { get; set; }

    /// <summary>
    /// How many games match the filters in all, across every batch. Only computed for the FIRST batch
    /// (no cursor); null on later ones, where the caller already has it. The library does not change
    /// size often enough for a per-batch recount to be worth a second query on every scroll.
    /// </summary>
    public int? Total { get; set; }
}
