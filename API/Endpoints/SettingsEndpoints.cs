using API.Services;
using RestSharp;

namespace API.Endpoints;

public static class SettingsEndpoints
{
    public static void AddSettingsEndpoints(this WebApplication app)
    {
        // Admin only, every route. Two of these are more sensitive than they look: /check-path
        // probes the server's filesystem, and /test-rawg-key makes the server issue an outbound
        // request with an attacker-chosen string. Neither should be reachable by a Contributor.
        var group = app.MapGroup("/settings")
            .RequireAuthorization("Admin")
            .WithTags("Settings")
            .WithOpenApi();

        group.MapGet("/", GetSettings)
             .Produces<SettingsContract>(StatusCodes.Status200OK)
             .WithName("GetSettings")
             .WithDescription("Current settings. Secrets are returned masked, never in full.");

        group.MapPatch("/", UpdateSettings)
             .Accepts<SettingsUpdateRequest>("application/json")
             .Produces<SettingsContract>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest)
             .WithName("UpdateSettings")
             .WithDescription("Applies a partial settings change and returns the new state");

        group.MapPost("/check-path", CheckPath)
             .Accepts<PathCheckRequest>("application/json")
             .Produces<PathCheckResult>(StatusCodes.Status200OK)
             .WithName("CheckSettingsPath")
             .WithDescription("Reports whether a server directory exists and is writable");

        group.MapPost("/test-rawg-key", TestRawgKey)
             .Produces<RawgKeyCheckResult>(StatusCodes.Status200OK)
             .WithName("TestRawgKey")
             .WithDescription("Makes one live RAWG call with the stored key");
    }

    private static async Task<IResult> GetSettings(SettingsService settings)
        => Results.Ok(await BuildContract(settings));

    private static async Task<SettingsContract> BuildContract(SettingsService settings)
    {
        var key = await settings.GetRawgApiKeyAsync();
        return new SettingsContract
        {
            GameFilesPath = await settings.GetGameFilesPathAsync(),
            MediaDirectory = settings.MediaDirectory,
            DataDirectory = settings.DataDirectory,
            RawgApiKeyIsSet = key is not null,
            RawgApiKeyMasked = Mask(key),
            AllowedFileTypes = [.. await settings.GetAllowedFileTypesAsync()],
        };
    }

    /// <summary>Last four characters only — enough to recognise a key, useless to steal.</summary>
    private static string? Mask(string? secret)
        => secret is null ? null : "\u2022\u2022\u2022\u2022" + (secret.Length <= 4 ? secret : secret[^4..]);

    private static async Task<IResult> UpdateSettings(SettingsUpdateRequest request, SettingsService settings)
    {
        if (request.GameFilesPath is not null)
        {
            var path = request.GameFilesPath.Trim();
            if (path.Length == 0)
                return Results.BadRequest("The archive directory cannot be blank.");
            if (!Path.IsPathRooted(path))
                return Results.BadRequest("The archive directory must be an absolute path.");

            // Validated here, not just in the browser. A path that cannot be created is worth
            // rejecting at save time rather than discovering at the first upload.
            var check = DirectoryProbe.Probe(path);
            if (!check.Writable)
                return Results.BadRequest($"That directory is not usable: {check.Reason}.");

            await settings.SetAsync(SettingsService.GameFilesPathKey, path);
        }

        if (request.ClearRawgApiKey)
        {
            await settings.SetAsync(SettingsService.RawgApiKeyKey, null);
        }
        else if (!string.IsNullOrWhiteSpace(request.RawgApiKey))
        {
            // Blank means "unchanged" rather than "clear", because the browser is never given the
            // current key and so cannot echo it back. Clearing is the explicit flag above.
            await settings.SetAsync(SettingsService.RawgApiKeyKey, request.RawgApiKey.Trim());
        }

        if (request.AllowedFileTypes is not null)
        {
            var normalised = SettingsService.ParseFileTypes(string.Join(',', request.AllowedFileTypes));
            if (normalised.Length == 0)
                return Results.BadRequest("At least one file type must be allowed, or nothing could be uploaded.");

            await settings.SetAsync(SettingsService.AllowedFileTypesKey, string.Join(',', normalised));
        }

        return Results.Ok(await BuildContract(settings));
    }

    private static IResult CheckPath(PathCheckRequest request)
        => Results.Ok(DirectoryProbe.Probe(request.Path));

    private static async Task<IResult> TestRawgKey(SettingsService settings)
    {
        var key = await settings.GetRawgApiKeyAsync();
        if (key is null)
        {
            return Results.Ok(new RawgKeyCheckResult { Reason = "not-configured" });
        }

        try
        {
            using var client = new RestClient();
            var request = new RestRequest("https://api.rawg.io/api/games", Method.Get)
                .AddParameter("key", key)
                .AddParameter("page_size", 1);
            var response = await client.ExecuteAsync(request);

            return Results.Ok(response.IsSuccessful
                ? new RawgKeyCheckResult { Ok = true, Reason = "ok" }
                : new RawgKeyCheckResult { Reason = response.StatusCode == 0 ? "unreachable" : "rejected" });
        }
        catch (Exception)
        {
            // No exception detail to the client: this is an outbound request the caller influenced,
            // and the message can carry proxy names, DNS state and internal addresses.
            return Results.Ok(new RawgKeyCheckResult { Reason = "unreachable" });
        }
    }
}
