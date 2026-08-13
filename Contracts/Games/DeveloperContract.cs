namespace GameTown.Contracts.Games;

/// <summary>
/// A studio credited with developing a game.
///
/// GamesCount and ImageBackground are gone with RAWG: both were RAWG catalogue statistics about the
/// studio's own page, neither ever reached a screen here, and IGDB's company records have no
/// equivalent worth carrying.
/// </summary>
public class DeveloperContract
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
}
