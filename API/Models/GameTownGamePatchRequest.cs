namespace API.Models
{
    public class GameTownGamePatchRequest
    {
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
        public int? RawgGameId { get; set; }

        /// <summary>
        /// A URL containing more details about the game.
        /// </summary>
        public string? Url { get; set; }
    }

}
