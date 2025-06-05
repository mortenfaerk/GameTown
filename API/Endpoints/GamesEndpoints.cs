using API.Models.Games;
using API.Services;
using Microsoft.AspNetCore.Mvc;

namespace API.Endpoints;

public static class GamesEndpoints
{
    public static void AddGamesTownGamesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/GTGames")
            .WithTags("GameTown Games")
            .WithOpenApi()
            .WithDescription("Endpoints for managing games in GameTown.");
        group.MapPost("/Add", AddGameWithFile)
            .Accepts<AddGameWithFileForm>("multipart/form-data")
            .RequireAuthorization("Contributor")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithName("AddGame")
            .WithDescription("Uploads a zipped game data file.")
            .DisableAntiforgery();
        group.MapGet("/{id}", GetGameById).Accepts<string>("text/plain")
            .Produces<ResponseGameTownGameDTO>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetGameById")
            .WithDescription("Retrieves a game from GameTown by its unique identifier. The ID should be a valid GUID format.");
        group.MapGet("/download/{id}", DownloadGame).Accepts<string>("text/plain")
            .Produces<FileStreamResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("DownloadGame")
            .WithDescription("Downloads a game from GameTown by its unique identifier. The ID should be a valid GUID format.");
        group.MapDelete("/{id}", RemoveGameById).Accepts<string>("text/plain")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("RemoveGameById")
            .WithDescription("Removes a game from GameTown by its unique identifier. The ID should be a valid GUID format.");
        group.MapPatch("/update", UpdateGame).Accepts<GameTownGamePatchRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithName("UpdateGame")
            .WithDescription("Updates an existing game in GameTown. The game data should be provided in the request body as JSON, including the unique identifier (ID) of the game to be updated.");
        group.MapGet("/getPaged/{page}/{pageSize}", GetGamesPaged).Accepts<int>("text/plain")
            .Accepts<int>("text/plain")
            .Produces<IEnumerable<ResponseGameTownGameDTO>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithName("GetGamesPaged")
            .WithDescription("Retrieves a paginated list of games from GameTown. The page and page size must be greater than zero.");
        group.MapGet("/search/", SearchGame).Accepts<GameTownGameSearchRequest>("text/plain")
            .Accepts<int>("text/plain")
            .Accepts<int>("text/plain")
            .Produces<IEnumerable<ResponseGameTownGameDTO>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithName("SearchGame")
            .WithDescription("Searches for games in GameTown based on a query string. The query string should not be empty and pagination parameters must be greater than zero.");
    }
    private static async Task<IResult> GetGameById(string id, GTGamesService service)
    {
        if (!Guid.TryParse(id, out var gameId))
            return Results.BadRequest("Invalid game ID format.");
        var game = await service.GetGameById(gameId);
        if (game == null)
            return Results.NotFound($"Game with ID {id} not found.");
        return Results.Ok(game);
    }
    private static async Task<IResult> RemoveGameById(string id, GTGamesService service)
    {
        if (!Guid.TryParse(id, out var gameId))
            return Results.BadRequest("Invalid game ID format.");
        try
        {
            await service.RemoveGameById(gameId);

        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
        return Results.NoContent();
    }
    private static async Task<IResult> UpdateGame(GameTownGamePatchRequest game, GTGamesService service)
    {
        if (!Guid.TryParse(game.Id, out var gameId))
            return Results.BadRequest("Invalid game ID format.");
        if (game == null)
            return Results.BadRequest("Game data cannot be null.");
        try
        {
            await service.UpdateGame(game);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
        return Results.NoContent();
    }
    private static async Task<IResult> GetGamesPaged(int page, int pageSize, GTGamesService service)
    {
        if (page < 1 || pageSize < 1)
            return Results.BadRequest("Page and page size must be greater than zero.");
        try
        {
            var games = await service.GetGamePaged(page, pageSize);
            return Results.Ok(games);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
    private static async Task<IResult> SearchGame(string query, int page, int pageSize, GTGamesService service)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest("Search query cannot be empty.");
        if (page < 1 || pageSize < 1)
            return Results.BadRequest("Page and page size must be greater than zero.");
        try
        {
            var games = await service.SearchGames(query, page, pageSize);
            return Results.Ok(games);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
    private static async Task<IResult> AddGameWithFile([FromForm] AddGameWithFileForm form, GTGamesService _gameService, FileService _fileService)
    {
        if (form.File == null)
            return Results.BadRequest("No file uploaded.");

        var allowedExtensions = new[] { ".zip", ".rar", ".7z" };
        var fileExtension = Path.GetExtension(form.File.FileName).ToLowerInvariant();

        if (!allowedExtensions.Contains(fileExtension))
            return Results.BadRequest("Invalid file format. Only .zip, .rar, and .7z files are allowed.");



        var filePath = _fileService.GetGameFilePath(form.File.FileName);
        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await form.File.CopyToAsync(stream);
        }

        var game = new RequestGameTownGameDTO
        {
            Title = form.Title,
            HowTo = form.HowTo,
            RAWGGameId = form.RAWGGameId
        };

        try
        {
            var fileSizeMb = form.File.Length / (1024.0 * 1024.0);
            await _gameService.AddGame(game, filePath, fileSizeMb);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
        return Results.NoContent();
    }
    private static async Task<IResult> DownloadGame(string id, GTGamesService service, FileService _fileService)
    {
        if (!Guid.TryParse(id, out var gameId))
            return Results.BadRequest("Invalid game ID format.");
        var gameUrl = await service.GetGameUrlById(gameId);
        if (gameUrl == null)
            return Results.NotFound($"Game with ID {id} not found.");
        if (!File.Exists(gameUrl))
            return Results.NotFound("Game file not found.");
        var stream = new FileStream(gameUrl, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Results.File(stream, "application/octet-stream", gameUrl);
    }
}
