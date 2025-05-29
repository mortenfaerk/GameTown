using API.Models;
using API.Services;
using EFModel.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Endpoints;

public static class GamesEndpoints
{
    public static void AddGamesEndpoints(this WebApplication app)
    {
        app.MapPost("games/add/", AddGame).WithDescription("Tilføjer nye spil til GameTown.").Produces<GameTownGameDTO>(StatusCodes.Status201Created).Produces<string>(StatusCodes.Status404NotFound).Produces<ProblemDetails>(StatusCodes.Status500InternalServerError).WithOpenApi(); ;
        app.MapGet("/games", LoadAllGames).WithDescription("Henter alle spil ud, med evt. tilhørende data fra RAWG api'et").Produces<ICollection<GameTownGameDTO>>(StatusCodes.Status200OK).WithOpenApi();
        app.MapGet("games/{id}", LoadGameById).WithDescription("Henter et enkelt spils informationer").Produces<GameTownGameDTO>(StatusCodes.Status200OK).WithOpenApi();
        app.MapDelete("/game", DeleteGame).WithDescription("Sletter et spil").WithOpenApi();
        app.MapPatch("/game/{id}", PatchGame).WithDescription("Opdater et spils metadata").Produces<GameTownGameDTO>(StatusCodes.Status200OK).Produces(StatusCodes.Status404NotFound).Produces<ProblemDetails>(StatusCodes.Status500InternalServerError).WithOpenApi();
    }
    private static async Task<IResult> LoadAllGames(DatabaseContext context, string? search)
    {
        IQueryable<GameTownGame> query = context.GameTownGames;

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(x => x.Title.Contains(searchLower, StringComparison.CurrentCultureIgnoreCase));
        }

        var gamesDto = await query.Select(x => new GameTownGameDTO
        {
            Id = x.Id,
            Title = x.Title,
            HowTo = x.HowTo,
            URL = x.Url,
            RAWGGame = x.Game != null
                ? new RAWGGameDTO(
                    x.Game.Id,
                    x.Game.Slug,
                    x.Game.Name,
                    x.Game.NameOriginal,
                    x.Game.Description,
                    x.Game.Metacritic,
                    x.Game.Released,
                    x.Game.Tba,
                    x.Game.Updated,
                    x.Game.BackgroundImage,
                    x.Game.BackgroundImageAdditional,
                    x.Game.Website,
                    x.Game.Rating,
                    x.Game.RatingTop,
                    x.Game.Playtime,
                    x.Game.ScreenshotsCount,
                    x.Game.MoviesCount,
                    x.Game.CreatorsCount,
                    x.Game.AchievementsCount,
                    x.Game.ParentAchievementsCount,
                    x.Game.RedditUrl,
                    x.Game.RedditCount,
                    x.Game.TwitchCount,
                    x.Game.YoutubeCount,
                    x.Game.ReviewsTextCount,
                    x.Game.RatingsCount,
                    x.Game.SuggestionsCount,
                    x.Game.MetacriticUrl,
                    x.Game.ParentsCount,
                    x.Game.AdditionsCount,
                    x.Game.GameSeriesCount,
                    x.Game.ReviewsCount,
                    x.Game.SaturatedColor,
                    x.Game.DominantColor
                  )
                : null
        }).ToListAsync();

        return Results.Ok(gamesDto);
    }
    private static async Task<IResult> LoadGameById(DatabaseContext context, string id)
    {
        Guid givenId = Guid.Parse(id);
        var gameDto = await context.GameTownGames
               .Where(x => x.Id == givenId)
               .Select(x => new GameTownGameDTO
               {
                   Id = x.Id,
                   Title = x.Title,
                   HowTo = x.HowTo,
                   URL = x.Url,
                   RAWGGame = x.Game != null
                       ? new RAWGGameDTO(
                           x.Game.Id,
                           x.Game.Slug,
                           x.Game.Name,
                           x.Game.NameOriginal,
                           x.Game.Description,
                           x.Game.Metacritic,
                           x.Game.Released,
                           x.Game.Tba,
                           x.Game.Updated,
                           x.Game.BackgroundImage,
                           x.Game.BackgroundImageAdditional,
                           x.Game.Website,
                           x.Game.Rating,
                           x.Game.RatingTop,
                           x.Game.Playtime,
                           x.Game.ScreenshotsCount,
                           x.Game.MoviesCount,
                           x.Game.CreatorsCount,
                           x.Game.AchievementsCount,
                           x.Game.ParentAchievementsCount,
                           x.Game.RedditUrl,
                           x.Game.RedditCount,
                           x.Game.TwitchCount,
                           x.Game.YoutubeCount,
                           x.Game.ReviewsTextCount,
                           x.Game.RatingsCount,
                           x.Game.SuggestionsCount,
                           x.Game.MetacriticUrl,
                           x.Game.ParentsCount,
                           x.Game.AdditionsCount,
                           x.Game.GameSeriesCount,
                           x.Game.ReviewsCount,
                           x.Game.SaturatedColor,
                           x.Game.DominantColor
                         )
                       : null
               })
               .SingleOrDefaultAsync();

        if (gameDto is null)
            return Results.NotFound();

        return Results.Ok(gameDto);
    }
    private static async Task<IResult> AddGame(DatabaseContext context,RAWGService rServ, GameTownGamePostRequest game)
    {
        try
        {
            var newGame = new GameTownGame
            {
                Title = game.Title,
                HowTo = game.HowTo,
                Url = game.Url,
                GameId = game.RawgGameId,
                Id = Guid.NewGuid()
            };
            if (game.RawgGameId != null)
            {
                newGame.Game = await GetRawgGameAsync(context,rServ,game.RawgGameId.Value);
            }

            context.GameTownGames.Add(newGame);
            await context.SaveChangesAsync();

            var newGameDto = new GameTownGameDTO
            {
                Id = newGame.Id,
                Title = newGame.Title,
                HowTo = newGame.HowTo,
                URL = newGame.Url,
                RAWGGame = newGame.Game != null ? new RAWGGameDTO(
                    newGame.Game.Id,
                    newGame.Game.Slug,
                    newGame.Game.Name,
                    newGame.Game.NameOriginal,
                    newGame.Game.Description,
                    newGame.Game.Metacritic,
                    newGame.Game.Released,
                    newGame.Game.Tba,
                    newGame.Game.Updated,
                    newGame.Game.BackgroundImage,
                    newGame.Game.BackgroundImageAdditional,
                    newGame.Game.Website,
                    newGame.Game.Rating,
                    newGame.Game.RatingTop,
                    newGame.Game.Playtime,
                    newGame.Game.ScreenshotsCount,
                    newGame.Game.MoviesCount,
                    newGame.Game.CreatorsCount,
                    newGame.Game.AchievementsCount,
                    newGame.Game.ParentAchievementsCount,
                    newGame.Game.RedditUrl,
                    newGame.Game.RedditCount,
                    newGame.Game.TwitchCount,
                    newGame.Game.YoutubeCount,
                    newGame.Game.ReviewsTextCount,
                    newGame.Game.RatingsCount,
                    newGame.Game.SuggestionsCount,
                    newGame.Game.MetacriticUrl,
                    newGame.Game.ParentsCount,
                    newGame.Game.AdditionsCount,
                    newGame.Game.GameSeriesCount,
                    newGame.Game.ReviewsCount,
                    newGame.Game.SaturatedColor,
                    newGame.Game.DominantColor
                ) : null
            };

            return Results.Created($"/games/{newGame.Id}", newGameDto);
        }
        catch (Exception ex)
        {
            return Results.Problem($"An error occurred: {ex.Message}");
        }
    }
    private static async Task<IResult> DeleteGame(DatabaseContext context, string id)
    {
        GameTownGame? game = await context.GameTownGames.FindAsync(id);
        if (game == null)
            return Results.NotFound("Game not found!");
        context.GameTownGames.Remove(game);
        await context.SaveChangesAsync();
        return Results.Ok();
    }
    private static async Task<IResult> PatchGame(DatabaseContext context,RAWGService rServ, Guid id, GameTownGamePatchRequest patchRequest)
    {
        var existingGame = await context.GameTownGames.FindAsync(id);
        if (existingGame is null)
        {
            return Results.NotFound();
        }
        if (patchRequest.Title is not null)
        {
            existingGame.Title = patchRequest.Title;
        }

        if (patchRequest.HowTo is not null)
        {
            existingGame.HowTo = patchRequest.HowTo;
        }

        if (patchRequest.RawgGameId is not null)
        {
            existingGame.Game = await GetRawgGameAsync(context,rServ,patchRequest.RawgGameId.Value);
            existingGame.GameId = patchRequest.RawgGameId;
        }

        if (patchRequest.Url is not null)
        {
            existingGame.Url = patchRequest.Url;
        }
        await context.SaveChangesAsync();
        var updatedDto = new GameTownGameDTO
        {
            Id = existingGame.Id,
            Title = existingGame.Title,
            HowTo = existingGame.HowTo,
            RawgGameId = existingGame.GameId,
            URL = existingGame.Url,
            RAWGGame = existingGame.Game != null
           ? new RAWGGameDTO(
               existingGame.Game.Id,
               existingGame.Game.Slug,
               existingGame.Game.Name,
               existingGame.Game.NameOriginal,
               existingGame.Game.Description,
               existingGame.Game.Metacritic,
               existingGame.Game.Released,
               existingGame.Game.Tba,
               existingGame.Game.Updated,
               existingGame.Game.BackgroundImage,
               existingGame.Game.BackgroundImageAdditional,
               existingGame.Game.Website,
               existingGame.Game.Rating,
               existingGame.Game.RatingTop,
               existingGame.Game.Playtime,
               existingGame.Game.ScreenshotsCount,
               existingGame.Game.MoviesCount,
               existingGame.Game.CreatorsCount,
               existingGame.Game.AchievementsCount,
               existingGame.Game.ParentAchievementsCount,
               existingGame.Game.RedditUrl,
               existingGame.Game.RedditCount,
               existingGame.Game.TwitchCount,
               existingGame.Game.YoutubeCount,
               existingGame.Game.ReviewsTextCount,
               existingGame.Game.RatingsCount,
               existingGame.Game.SuggestionsCount,
               existingGame.Game.MetacriticUrl,
               existingGame.Game.ParentsCount,
               existingGame.Game.AdditionsCount,
               existingGame.Game.GameSeriesCount,
               existingGame.Game.ReviewsCount,
               existingGame.Game.SaturatedColor,
               existingGame.Game.DominantColor
           )
           : null
        };
        return Results.Ok(updatedDto);
    }
    private static async Task<Game> GetRawgGameAsync(DatabaseContext context, RAWGService rServ, int id)
    {
        var resultingGame = await rServ.GetGameById(id) ?? throw new Exception($"Couldn't find game of {id}");
        var existingGame = await context.Games.FindAsync(resultingGame.Id);
        if (existingGame != null)
        {
            context.Entry(existingGame).CurrentValues.SetValues(resultingGame);
        }
        else
        {
            context.Games.Add(resultingGame);
        }
        await context.SaveChangesAsync();
        return resultingGame;
    }
}
