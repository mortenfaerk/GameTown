using API.Models;
using API.Services;
using EFModel.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Endpoints;

public static class GamesEndpoints
{
    public static void AddGamesEndpoints(this WebApplication app)
    {
        app.MapPost("/game/", UpdateGame);
        app.MapGet("/games", LoadAllGames);
        app.MapGet("games/{id}", LoadGameById);
        app.MapDelete("/game", DeleteGame);
        app.MapPatch("/game", UpdateGame);
    }
    public static async Task<IResult> LoadAllGames(DatabaseContext context, string? search)
    {
        var games = await context.GameTownGames.Include(x=>x.Game).Include(x=>x.Game.Genres).Include(x=>x.Game.Developers).ToListAsync();

        if(string.IsNullOrWhiteSpace(search) == false)
        {
            games.RemoveAll(x=> !x.Title.Contains(search,StringComparison.OrdinalIgnoreCase));
        }
        return Results.Ok(games);
    }
    public static async Task<IResult> LoadGameById(DatabaseContext context, string id)
    {
        var game = await context.GameTownGames.Include(x => x.Game).Include(x => x.Game.Genres).Include(x => x.Game.Developers).SingleOrDefaultAsync(x => x.Id.ToString() == id);
        if(game is null)
            return Results.NotFound();
        return Results.Ok(game);
    }
    public static async Task<IResult> AddGame(DatabaseContext context,RAWGService rServ, GameTownGamePostRequest game)
    {
         try
            {
                GameTownGame newGame = new() { Title = game.Title, HowTo = game.HowTo, Url = game.Url, GameId = game.GameId, Id = Guid.NewGuid()};
                if (game.GameId != null) { 
                    Game? resultingGame = await rServ.GetGameById(game.GameId.Value);
                    if (resultingGame == null)
                    {
                        return Results.NotFound($"Game with ID {game.GameId.Value} not found.");
                    }
                    var existingGame = await context.Games.FindAsync(resultingGame.Id);
                    if(existingGame != null)
                    {
                        context.Entry(existingGame).CurrentValues.SetValues(resultingGame);

                    }
                    else
                    {
                        context.Games.Add(resultingGame);
                    }
                   
                }
                context.GameTownGames.Add(newGame);
                await context.SaveChangesAsync();
                return Results.Created($"/games/{newGame.Id}", newGame);
            }
            catch (Exception ex)
            {
                return Results.Problem($"An error occurred: {ex.Message}");
            }
    }
    public static async Task<IResult> DeleteGame(DatabaseContext context, string id)
    {
        GameTownGame? game = await context.GameTownGames.FindAsync(id);
        if (game == null)
            return Results.NotFound("Game not found!");
        context.GameTownGames.Remove(game);
        await context.SaveChangesAsync();
        return Results.Ok();
    }
    public static async Task<IResult> UpdateGame(DatabaseContext context, GameTownGamePostRequest game)
    {
        throw new NotImplementedException();
    }
}
