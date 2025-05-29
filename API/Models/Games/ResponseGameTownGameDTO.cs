using EFModel.Models;

namespace API.Models.Games;

public class ResponseGameTownGameDTO
{
    public Guid Id { get; set; }
    public string Title { get; set; }
    public string HowTo { get; set; }
    public string URL { get; set; }
    public ResponseRAWGGameDTO? RAWGGame { get; set; }

    public ResponseGameTownGameDTO(GameTownGame game)
    {
        Id = game.Id;
        Title = game.Title;
        HowTo = game.HowTo;
        URL = game.Url;
        RAWGGame = game.Rawggame != null ? new ResponseRAWGGameDTO(game.Rawggame) : null;

    }
}
