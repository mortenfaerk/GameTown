using API.Models.Games;
using API.Services;

namespace API.Endpoints
{
    public static class MetaDataEndpoints
    {
        public static void AddMetaDataEndpoints(this WebApplication app)
        {
               // These proxy the RAWG API using our key, and only contributor screens call them
               // (the add-game picker and the RAWG browser). Gating the group keeps the key from
               // being spent by anyone who can reach the host.
               var group = app.MapGroup("meta")
                .WithTags("Metadata")
                     .RequireAuthorization("Contributor")
                     .WithOpenApi()
                     .WithDescription("Endpoints for managing metadata related to the application.");
               group.MapGet("/searchMetadata", SearchGame)
                .Accepts<string>("text/plain")
                .Produces<List<RawgGameContract>>(StatusCodes.Status200OK)
                .Produces(StatusCodes.Status400BadRequest)
                .Produces(StatusCodes.Status500InternalServerError)
                .WithName("SearchMetadata")
                .WithDescription("Searches for games based on a query string. The query string should not be empty and pagination parameters must be greater than zero.");
               group.MapGet("/getGame/{gameid}", GetGame)
                .Accepts<string>("text/plain")
                .Produces<RawgGameContract>(StatusCodes.Status200OK)
                .Produces(StatusCodes.Status400BadRequest)
                .Produces(StatusCodes.Status500InternalServerError)
                .WithName("GetGame")
                .WithDescription("Retrieves a game by its unique identifier. The ID should not be empty.");
        }

        private static async Task<IResult> SearchGame(string query, int page, int pageSize, RAWGService rawgService)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Results.BadRequest("Search query cannot be empty.");
            if (page < 1 || pageSize < 1)
                return Results.BadRequest("Page and page size must be greater than zero.");
            try
            {
                var games = await rawgService.SearchGames(query, page, pageSize);
                return Results.Ok(games);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        }
        private static async Task<IResult> GetGame(string gameid, RAWGService rawgService)
        {
            if(string.IsNullOrEmpty(gameid))
                return Results.BadRequest("Game ID cannot be empty.");
            try
            {
                var game = await rawgService.GetGameById(gameid);
                // Map to the contract rather than returning the EF entity: the contract is where
                // the description gets sanitised, and it avoids serialising navigation collections.
                return game is null ? Results.NotFound($"Game with ID {gameid} not found.") : Results.Ok(game.ToContract());
            }catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        }
    }
}
