namespace API.Models;

public class GameTownGameDTO
{
    public required Guid Id { get; set; }
    public required  string Title { get; set; }
    public required string HowTo { get; set; }
    public int? RawgGameId { get; set; }
    public required string URL { get; set; }
    public RAWGGameDTO? RAWGGame { get; set; }
}
