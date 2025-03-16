using API.Models;
using API.Services;
using EFModel.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Endpoints
{
    public static class GamesEndpoints
    {
        public static void MapGamesEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost("/game/", async (DatabaseContext context, RAWGService rServ, GameTownGamePostRequest game) =>
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
            });
            app.MapGet("/games", async (DatabaseContext context) =>
            {
                var games = await context.GameTownGames.ToListAsync();
                return Results.Ok(games);
            });
            app.MapDelete("/game", async (DatabaseContext context, string id) =>
            {
                GameTownGame? game = await context.GameTownGames.FindAsync(id);
                if (game == null)
                    return Results.NotFound("Game not found!");
                context.GameTownGames.Remove(game);
                await context.SaveChangesAsync();
                return Results.Ok();
            });
            app.MapPatch("/game", async (DatabaseContext context, GameTownGamePostRequest game) =>
            {
                throw new NotImplementedException();
            });
        }
    }

}
