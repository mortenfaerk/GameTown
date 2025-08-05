namespace API.Models.Games
{
    public class RequestGameTownGameDTO
    {
        public required string Title { get; set; }
        public required string HowTo { get; set; }
        public string? RAWGGameId { get; set; }

    }
}
