namespace API.Models.Games
{
    public class RequestGameTownGameDTO
    {
        public required Guid Id { get; set; }
        public required string Title { get; set; }
        public required string HowTo { get; set; }
        public required string URL { get; set; }
        public int? RAWGGameId { get; set; }
    }
}
