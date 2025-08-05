namespace API.Models.Games
{
    public class GameTownGamePatchRequest
    {
        /// <summary>
        /// The unique identifier for the game. Must be a valid GUID format.
        /// </summary>
        public required string Id { get; set; }
        /// <summary>
        /// The title of the game.
        /// </summary>
        public string? Title { get; set; }

        /// <summary>
        /// Instructions on how to play/setup the game.
        /// </summary>
        public string? HowTo { get; set; }

        /// <summary>
        /// The ID of the referenced game (if applicable).
        /// </summary>
        public string? RawgGameId { get; set; }

        /// <summary>
        /// A URL containing more details about the game.
        /// </summary>
        public string? Url { get; set; }
    }

}
