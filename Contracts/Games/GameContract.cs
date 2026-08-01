namespace GameTown.Contracts.Games;

/// <summary>A game in the GameTown library, optionally enriched with RAWG metadata.</summary>
public class GameContract
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string HowTo { get; set; } = string.Empty;

    /// <summary>Size of the uploaded archive, in megabytes.</summary>
    public double Size { get; set; }

    /// <summary>
    /// Locally stored portrait box art, as "/media/{guid}.{ext}", or null if none has been chosen.
    ///
    /// Null is the normal state, not an error: the reader falls back to the RAWG background image,
    /// then the first screenshot, then the title's initials. RAWG has no box art field at all — this
    /// exists because the wide promotional still it does supply is the wrong picture for a shelf.
    /// </summary>
    public string? BoxArtUrl { get; set; }

    /// <summary>Manual tags. Always present, possibly empty. <see cref="TagContract.GameCount"/> is not populated here.</summary>
    public List<TagContract> Tags { get; set; } = [];

    /// <summary>
    /// Whether this game's instructions have been written into its archive as GameTownGuide.txt.
    ///
    /// The archive holds a copy; <see cref="HowTo"/> is the original. Editing the instructions with
    /// this set rewrites the copy.
    /// </summary>
    public bool GuideBaked { get; set; }

    /// <summary>
    /// Whether the archive's format allows a guide to be added to it at all — in practice, whether it
    /// is a ZIP.
    ///
    /// A computed flag rather than the file name, because the stored path is a server location and has
    /// no business reaching the browser. It exists so the toggle can be shown disabled with a reason
    /// instead of silently vanishing for some uploads and not others.
    /// </summary>
    public bool CanBakeGuide { get; set; }

    public RawgGameContract? RawgGame { get; set; }
}
