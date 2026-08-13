using API.Mapping;
using API.Services.Metadata;
using GameTown.Contracts.Games;

namespace API.Endpoints
{
    public static class MetaDataEndpoints
    {
        public static void AddMetaDataEndpoints(this WebApplication app)
        {
            // These proxy the metadata provider using our credentials, and only contributor screens
            // call them (the add-game picker and the metadata browser). Gating the group keeps the
            // rate limit from being spent by anyone who can reach the host.
            var group = app.MapGroup("meta")
                 .WithTags("Metadata")
                 .RequireAuthorization("Contributor")
                 .WithDescription("Endpoints for managing metadata related to the application.");

            group.MapGet("/searchMetadata", SearchGame)
                 .Produces<MetadataSearchResponse>(StatusCodes.Status200OK)
                 .Produces(StatusCodes.Status400BadRequest)
                 .WithName("SearchMetadata")
                 .WithDescription("Searches the metadata provider. The query must not be empty and pagination parameters must be greater than zero.");

            group.MapGet("/getGame/{gameid}", GetGame)
                 .Produces<GameMetadataContract>(StatusCodes.Status200OK)
                 .Produces(StatusCodes.Status400BadRequest)
                 .Produces(StatusCodes.Status404NotFound)
                 .WithName("GetGame")
                 .WithDescription("Retrieves one game from the metadata provider by its provider id.");
        }

        /// <summary>
        /// Answers 200 with a reason code rather than a 500 when the provider is unavailable.
        ///
        /// "No credentials configured" is the normal state of a fresh install, not a server fault, and
        /// the picker has to be able to say so in words. The old handler wrapped everything in a
        /// try/catch that turned a missing key into a 500 with the exception message as the body.
        /// </summary>
        private static async Task<IResult> SearchGame(
            string query, int page, int pageSize, IGameMetadataProvider provider, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Results.BadRequest("Search query cannot be empty.");
            if (page < 1 || pageSize < 1)
                return Results.BadRequest("Page and page size must be greater than zero.");

            var response = await provider.SearchAsync(query, page, pageSize, cancellationToken);

            return Results.Ok(new MetadataSearchResponse
            {
                Results = [.. response.Results.Select(r => r.ToContract())],
                Reason = response.Reason,
                ProviderName = provider.DisplayName
            });
        }

        /// <summary>
        /// Fetches one game from the provider WITHOUT storing it.
        ///
        /// It is mapped straight from the provider record rather than through the database, so the
        /// picker can preview a candidate the library has never seen. Id is 0 for that reason: there
        /// is no local row yet, and the caller addresses the game by ExternalId until one exists.
        /// </summary>
        private static async Task<IResult> GetGame(
            string gameid, IGameMetadataProvider provider, CancellationToken cancellationToken)
        {
            if (!int.TryParse(gameid, out var externalId) || externalId <= 0)
                return Results.BadRequest("Game ID must be a positive integer.");

            var game = await provider.GetAsync(externalId, cancellationToken);
            if (game is null) return Results.NotFound($"Game with ID {gameid} not found.");

            return Results.Ok(new GameMetadataContract
            {
                Id = 0,
                Provider = game.Provider,
                ExternalId = game.ExternalId,
                Slug = game.Slug,
                Name = game.Name,
                // Sanitised on the way out exactly as a stored record is. This one has never been
                // through the database, so nothing else would have done it.
                Description = GameMappings.SanitizeDescription(game.Description),
                Released = game.Released,
                CriticScore = game.CriticScore,
                Rating = game.Rating,
                // Still the provider's CDN URL: nothing is downloaded until the game is actually
                // added. The client renders it directly, which is why ResolveMedia on the client side
                // has to keep passing absolute URLs through untouched.
                Image = game.ImageUrl,
                Website = game.Website,
                Screenshots = [.. game.Screenshots.Select(s => new ScreenshotContract
                {
                    Id = s.ExternalId,
                    Image = s.ImageUrl,
                    Width = s.Width,
                    Height = s.Height
                })],
                Developers = [.. game.Developers.Select(d => new DeveloperContract
                {
                    Id = d.ExternalId,
                    Name = d.Name,
                    Slug = d.Slug
                })],
                Genres = [.. game.Genres.Select(g => new GenreContract
                {
                    Id = g.ExternalId,
                    Name = g.Name,
                    Slug = g.Slug
                })]
            });
        }
    }
}
