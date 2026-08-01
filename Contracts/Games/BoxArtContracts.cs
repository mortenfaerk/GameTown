using System.ComponentModel.DataAnnotations;

namespace GameTown.Contracts.Games;

/// <summary>One piece of candidate box art offered by the artwork provider.</summary>
public class BoxArtCandidateContract
{
    /// <summary>Small preview, for the picker grid. May be the same as <see cref="FullUrl"/>.</summary>
    public string ThumbUrl { get; set; } = string.Empty;

    /// <summary>What gets downloaded and stored if this candidate is chosen.</summary>
    public string FullUrl { get; set; } = string.Empty;

    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>Where it came from, e.g. "SteamGridDB". Shown as attribution in the picker.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// The outcome of a box-art search.
///
/// A wrapper rather than a bare list because "no artwork provider is configured" and "that title has
/// no artwork" need different words in the UI, and an empty array cannot tell them apart. The same
/// reasoning as <c>RawgKeyCheckResult</c>.
/// </summary>
public class BoxArtSearchResult
{
    public List<BoxArtCandidateContract> Candidates { get; set; } = [];

    /// <summary>
    /// A fixed code, never an exception message: "ok", "not-configured", "no-match", "rejected"
    /// (the provider refused the key) or "unreachable". This endpoint reports on an outbound request,
    /// and raw exception text discloses proxy names and internal addresses.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Adopts an image from a URL as a game's box art.
///
/// The server downloads it and stores a local copy; the URL is never persisted. That keeps the shelf
/// working with no internet and stops a third-party host from seeing every visitor to the library.
/// </summary>
public class SetBoxArtRequest
{
    [Required]
    public string Url { get; set; } = string.Empty;
}
