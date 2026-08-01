using API.Models.Games;
using API.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace API.Endpoints;

public static class GamesEndpoints
{
    public static void AddGamesTownGamesEndpoints(this WebApplication app)
    {
        // NB: no .Accepts<T>() on the GET routes below, and do not add any.
        //
        // Accepts describes a request BODY, and it applies a content-type constraint to the endpoint.
        // A GET carries no body and no Content-Type, so `.Accepts<int>("text/plain")` made these
        // routes unmatchable by any normal client — including this app's own HttpClient.
        //
        // That used to surface as a 404. Since the API began serving the SPA it is far worse: the
        // request falls through to MapFallbackToFile and comes back 200 text/html, so the caller sees
        // success and tries to parse the SPA shell as JSON.
        var group = app.MapGroup("/GTGames")
            .WithTags("GameTown Games")
            .WithDescription("Endpoints for managing games in GameTown.");
        group.MapPost("/Add", AddGameWithFile)
            // Describes the body for OpenAPI only. The handler does NOT model-bind it — see
            // AddGameWithFile — because binding an IFormFile buffers the whole archive to a temp
            // file first. AddGameWithFileForm exists purely to keep the documented shape honest.
            .Accepts<AddGameWithFileForm>("multipart/form-data")
            .RequireAuthorization("Contributor")
            .Produces<AddGameResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithName("AddGame")
            .WithDescription("Uploads a zipped game data file.")
            .DisableAntiforgery();
        // A literal segment, so routing prefers it over "/{id}" below — the same reason
        // "/download/{id}" works. Contributor rather than anonymous: only uploaders need it, and it
        // reports how the server is configured.
        group.MapGet("/upload-limits", GetUploadLimits)
            .RequireAuthorization("Contributor")
            .Produces<UploadLimitsContract>(StatusCodes.Status200OK)
            .WithName("GetUploadLimits")
            .WithDescription("The upload size ceiling and file-type allowlist currently in force.");
        // Browsing and downloading are public by design (anyone on the LAN); everything that
        // mutates requires Contributor. The global fallback policy means the reads below have to
        // opt out explicitly.
        group.MapGet("/{id}", GetGameById)
            .AllowAnonymous()
            .Produces<GameContract>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetGameById")
            .WithDescription("Retrieves a game from GameTown by its unique identifier. The ID should be a valid GUID format.");
        group.MapGet("/download/{id}", DownloadGame)
            .AllowAnonymous()
            .Produces<FileStreamResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("DownloadGame")
            .WithDescription("Downloads a game from GameTown by its unique identifier. The ID should be a valid GUID format.");
        group.MapDelete("/{id}", RemoveGameById)
            .RequireAuthorization("Contributor")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("RemoveGameById")
            .WithDescription("Removes a game from GameTown by its unique identifier. The ID should be a valid GUID format.");
        group.MapPatch("/update", UpdateGame).Accepts<GameTownGamePatchRequest>("application/json")
            .RequireAuthorization("Contributor")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithName("UpdateGame")
            .WithDescription("Updates an existing game in GameTown. The game data should be provided in the request body as JSON, including the unique identifier (ID) of the game to be updated.");
        group.MapGet("/getPaged/{page}/{pageSize}", GetGamesPaged)
            .AllowAnonymous()
            .Produces<IEnumerable<GameContract>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithName("GetGamesPaged")
            .WithDescription("Retrieves a paginated list of games from GameTown. The page and page size must be greater than zero.");
        group.MapGet("/search/", SearchGame)
            .AllowAnonymous()
            .Produces<IEnumerable<GameContract>>(StatusCodes.Status200OK)
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
    /// <summary>
    /// Splits the optional "tags" query parameter into slugs.
    ///
    /// Comma-separated in one parameter rather than repeated ("?tags=lan&amp;tags=co-op"), because the
    /// value goes straight into the library page's own URL — "/?tags=lan,co-op" is something a person
    /// can read, edit and paste into chat, which is the whole reason the filter lives in the URL.
    ///
    /// An unrecognised slug is not an error: it simply matches nothing, so a stale link degrades to an
    /// empty shelf rather than a 400.
    /// </summary>
    private static string[] ParseTags(string? tags)
        => string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<IResult> GetGamesPaged(int page, int pageSize, GTGamesService service, string? tags = null)
    {
        if (page < 1 || pageSize < 1)
            return Results.BadRequest("Page and page size must be greater than zero.");
        try
        {
            var games = await service.GetGamePaged(page, pageSize, ParseTags(tags));
            return Results.Ok(games);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
    private static async Task<IResult> SearchGame(
        string query, int page, int pageSize, GTGamesService service, string? tags = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest("Search query cannot be empty.");
        if (page < 1 || pageSize < 1)
            return Results.BadRequest("Page and page size must be greater than zero.");
        try
        {
            var games = await service.SearchGames(query, page, pageSize, ParseTags(tags));
            return Results.Ok(games);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
    private static async Task<IResult> GetUploadLimits(SettingsService _settings)
        => Results.Ok(new UploadLimitsContract
        {
            MaxUploadSizeMb = await _settings.GetMaxUploadSizeMbAsync(),
            AllowedFileTypes = [.. await _settings.GetAllowedFileTypesAsync()],
        });

    /// <summary>
    /// Takes HttpContext rather than a bound [FromForm] model on purpose.
    ///
    /// Model binding an IFormFile spools the entire archive to Path.GetTempPath() before the handler
    /// runs, and the handler then had to copy it again into GameFilesPath — a second full pass over
    /// every byte that could only begin after the last one arrived. That silent window is what a
    /// reverse proxy in front of GameTown times out on. ArchiveUpload streams the body to its final
    /// location instead; see that class for the full reasoning.
    /// </summary>
    private static async Task<IResult> AddGameWithFile(
        HttpContext context, GTGamesService _gameService, FileService _fileService, SettingsService _settings)
    {
        var limitBytes = await _settings.GetMaxUploadSizeBytesAsync();

        // Kestrel's own ceiling, raised or lowered per request from the admin setting. Set before a
        // byte of the body is read, because the feature goes read-only once reading starts.
        //
        // ArchiveUpload counts bytes as well and would catch an oversized upload on its own; this is
        // here because Kestrel can abort at the transport level, without the application having to
        // read and discard the rest of a body it has already decided to reject. Absent under
        // TestServer, hence the null check.
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = limitBytes;

        // Fast path for the common case of a client that declares its size honestly: reject before
        // reading rather than after receiving the whole thing.
        if (limitBytes is not null && context.Request.ContentLength > limitBytes)
            return Results.Json(ArchiveUpload.TooLargeMessage(limitBytes.Value),
                statusCode: StatusCodes.Status413PayloadTooLarge);

        var upload = await ArchiveUpload.ReadAsync(
            context.Request, _fileService, _settings, context.RequestAborted);

        if (!upload.Success)
            return Results.Json(upload.Error, statusCode: upload.StatusCode);

        // Validated after the body has been read, not before: the title arrives in that same body.
        // Previously a missing title reached the database and came back as a 500.
        var title = upload.Field("title")?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            TryDeleteArchive(upload.StoredPath);
            return Results.BadRequest("A title is required.");
        }

        // The dedupe guard. Its motivating case is a retry that should never have happened: a long
        // upload outlives a reverse proxy's read timeout, the browser reports a network error, the
        // server completes anyway, and the contributor uploads the same archive again — ending up
        // with two library entries and two copies on disk.
        //
        // Checked here rather than up front because the identity being matched is the file's
        // content, which is not known until the last byte has arrived. The upload is therefore not
        // saved by this, only the library and the disk.
        //
        // Not enforced by a UNIQUE index: that would turn two people uploading the same archive in
        // the same instant into a 500, and a retry — the case this exists for — is sequential by
        // definition, so the read-then-write window is not one a real workflow reaches.
        var duplicateOf = await _gameService.FindTitleByArchiveHash(upload.Sha256);
        if (duplicateOf is not null)
        {
            TryDeleteArchive(upload.StoredPath);
            return Results.Json(
                $"That exact archive is already in the library as '{duplicateOf}'. "
                + "If an earlier upload appeared to fail, it did not — the game is there. "
                + "Delete that entry first if you meant to replace it.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var game = new RequestGameTownGameDTO
        {
            Title = title,
            HowTo = upload.Field("howTo") ?? string.Empty,
            RAWGGameId = upload.Field("rawgGameId")
        };

        Guid newGameId;
        try
        {
            newGameId = await _gameService.AddGame(
                game, upload.StoredPath, upload.Bytes / (1024.0 * 1024.0), upload.Sha256);
        }
        catch (KeyNotFoundException ex)
        {
            // The archive is already on disk at this point. Nothing references it if the row was not
            // written, so leaving it would silently consume the disk this endpoint exists to fill
            // carefully.
            TryDeleteArchive(upload.StoredPath);
            return Results.NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            TryDeleteArchive(upload.StoredPath);
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }

        // 201 with the new id, not the 204 this used to answer. Tags and box art are set by separate
        // calls, and with no id in the response the add-game screen had nothing to address them to —
        // the contributor had to go and find the game again to finish describing it.
        //
        // Still a success status, so nothing that only checked for 2xx changes behaviour; the SPA's
        // XHR upload path goes through ApiResult.FromStatus, which treats any 2xx as success.
        return Results.Created($"/GTGames/{newGameId}", new AddGameResponse { Id = newGameId });
    }

    private static void TryDeleteArchive(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
    private static async Task<IResult> DownloadGame(string id, GTGamesService service, FileService _fileService)
    {
        if (!Guid.TryParse(id, out var gameId))
            return Results.BadRequest("Invalid game ID format.");
        var gameFile = await service.GetGameFileById(gameId);
        if (gameFile == null)
            return Results.NotFound($"Game with ID {id} not found.");

        // Never open a stored path directly — it must be proven to live inside GameFilesPath.
        var (resolved, resolvedPath) = await _fileService.TryResolveGameFileAsync(gameFile.Url);
        if (!resolved || !File.Exists(resolvedPath))
            return Results.NotFound("Game file not found.");

        var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Previously the absolute server path was handed over as the download name, which both
        // looked broken to the user and disclosed the server's directory layout.
        var invalid = Path.GetInvalidFileNameChars();
        var safeTitle = new string(gameFile.Title.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        var downloadName = $"{safeTitle}{Path.GetExtension(resolvedPath)}";

        return Results.File(stream, "application/octet-stream", downloadName);
    }
}
