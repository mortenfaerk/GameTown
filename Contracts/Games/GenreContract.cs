namespace GameTown.Contracts.Games;

/// <summary>
/// A genre, as the metadata provider classifies it — what a game *is*.
///
/// Distinct from a <see cref="TagContract"/>, which says how a game gets played. Genres are imported
/// and read-only; tags are typed by people here and are the thing the shelf actually gets filtered on.
/// </summary>
public class GenreContract
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
}
