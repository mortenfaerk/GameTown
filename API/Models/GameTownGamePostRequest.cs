namespace API.Models;

/// <summary>
/// Represents the data required to create a new game.
/// </summary>
public class GameTownGamePostRequest(string title, string howto, int? gameId, string url)
{
    /// <summary>
    /// The title of the game.
    /// </summary>
    public string Title { get; set; } = title;
    /// <summary>
    /// Instructions on how to play/setup the game.
    /// </summary>
    public string HowTo { get; set; } = howto;
    /// <summary>
    /// The ID of the referenced game (if applicable). When used the application will try to gather the relevant metadata for the game online. 
    /// </summary>
    public int? GameId { get; set; } = gameId;
    /// <summary>
    /// A URL containing more details about the game.
    /// </summary>
    public string Url { get; set; } = url;
}
