using API.Services;

namespace API.Endpoints
{
    public static class MetaDataEndpoints
    {
        public static void AddMetaDataEndpoints(this WebApplication app)
        {
               var group = app.MapGroup("meta")
                .WithTags("Metadata")
                     .WithOpenApi()
                     .WithDescription("Endpoints for managing metadata related to the application.");
               group.MapGet("/searchMetadata", SearchGame)
                .Accepts<string>("text/plain")
                .Produces(StatusCodes.Status200OK)
                .Produces(StatusCodes.Status400BadRequest)
                .Produces(StatusCodes.Status500InternalServerError)
                .WithName("SearchMetadata")
                .WithDescription("Searches for games based on a query string. The query string should not be empty and pagination parameters must be greater than zero.");


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
    }
}
