namespace API.Models.Games
{
    public class RequestGameTownGameDTO
    {
        public required string Title { get; set; }
        public required string HowTo { get; set; }
        /// <summary>The metadata provider's own id for the game, or null. See AddGameRequest.</summary>
        public string? ProviderGameId { get; set; }

    }
}
