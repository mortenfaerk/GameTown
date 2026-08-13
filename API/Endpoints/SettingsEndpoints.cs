using API.Services;
using API.Services.BoxArt;
using API.Services.Metadata;
using EFModel.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Endpoints;

public static class SettingsEndpoints
{
    public static void AddSettingsEndpoints(this WebApplication app)
    {
        // Admin only, every route. Two of these are more sensitive than they look: /check-path
        // probes the server's filesystem, and the credential tests make the server issue outbound
        // requests. Neither should be reachable by a Contributor.
        var group = app.MapGroup("/settings")
            .RequireAuthorization("Admin")
            .WithTags("Settings");

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

        group.MapPost("/test-igdb-credentials", TestIgdbCredentials)
             .Produces<ProviderCredentialCheckResult>(StatusCodes.Status200OK)
             .WithName("TestIgdbCredentials")
             .WithDescription("Makes one live IGDB call with the stored credentials");

        group.MapPost("/test-boxart-key", TestBoxArtKey)
             .Produces<ProviderCredentialCheckResult>(StatusCodes.Status200OK)
             .WithName("TestBoxArtKey")
             .WithDescription("Makes one live artwork-provider call with the stored key");
    }

    private static async Task<IResult> GetSettings(
        SettingsService settings, DatabaseContext context, IGameMetadataProvider provider)
        => Results.Ok(await BuildContract(settings, context, provider));

    /// <summary>
    /// Whether this library still carries metadata from a provider that no longer answers.
    ///
    /// Only asked when there are no IGDB credentials, and only to decide whether to show the upgrade
    /// banner. Any provider other than the current one counts, so this does not need editing the next
    /// time a source is retired.
    /// </summary>
    private static Task<bool> HasRetiredMetadata(DatabaseContext context, IGameMetadataProvider provider)
        => context.MetadataGames.AsNoTracking().AnyAsync(m => m.Provider != provider.Id);

    private static async Task<SettingsContract> BuildContract(
        SettingsService settings, DatabaseContext context, IGameMetadataProvider provider)
    {
        var (clientId, clientSecret) = await settings.GetIgdbCredentialsAsync();
        var boxArtKey = await settings.GetBoxArtApiKeyAsync();
        return new SettingsContract
        {
            GameFilesPath = await settings.GetGameFilesPathAsync(),
            MediaDirectory = settings.MediaDirectory,
            DataDirectory = settings.DataDirectory,
            IgdbCredentialsAreSet = clientId is not null && clientSecret is not null,
            // The raw read, not the pair: this shows the half-configured state on purpose, so an
            // admin who saved an id but no secret can see what happened.
            IgdbClientId = await settings.GetIgdbClientIdAsync(),
            IgdbClientSecretMasked = Mask(clientSecret),
            HasRetiredProviderMetadata = clientSecret is null && await HasRetiredMetadata(context, provider),
            BoxArtApiKeyIsSet = boxArtKey is not null,
            BoxArtApiKeyMasked = Mask(boxArtKey),
            AllowedFileTypes = [.. await settings.GetAllowedFileTypesAsync()],
            MaxUploadSizeMb = await settings.GetMaxUploadSizeMbAsync(),
        };
    }

    /// <summary>Last four characters only — enough to recognise a key, useless to steal.</summary>
    private static string? Mask(string? secret)
        => secret is null ? null : "\u2022\u2022\u2022\u2022" + (secret.Length <= 4 ? secret : secret[^4..]);

    private static async Task<IResult> UpdateSettings(
        SettingsUpdateRequest request, SettingsService settings, DatabaseContext context,
        IGameMetadataProvider provider)
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

        if (request.ClearIgdbCredentials)
        {
            // Both halves together. They are one credential, and a client id left behind without its
            // secret is a half-configured state that reports itself as unconfigured — visible in the
            // UI, useless to the provider, and confusing to whoever finds it.
            await settings.SetAsync(SettingsService.IgdbClientIdKey, null);
            await settings.SetAsync(SettingsService.IgdbClientSecretKey, null);
        }
        else
        {
            // Blank means "unchanged" rather than "clear", because the browser is never given the
            // current secret and so cannot echo it back. Clearing is the explicit flag above.
            //
            // The two halves are set independently so an admin can rotate just the secret — Twitch
            // regenerates secrets without changing the client id, and forcing both to be retyped
            // would invite the id to be retyped wrongly.
            if (!string.IsNullOrWhiteSpace(request.IgdbClientId))
                await settings.SetAsync(SettingsService.IgdbClientIdKey, request.IgdbClientId.Trim());

            if (!string.IsNullOrWhiteSpace(request.IgdbClientSecret))
                await settings.SetAsync(SettingsService.IgdbClientSecretKey, request.IgdbClientSecret.Trim());
        }

        if (request.ClearBoxArtApiKey)
        {
            await settings.SetAsync(SettingsService.BoxArtApiKeyKey, null);
        }
        else if (!string.IsNullOrWhiteSpace(request.BoxArtApiKey))
        {
            await settings.SetAsync(SettingsService.BoxArtApiKeyKey, request.BoxArtApiKey.Trim());
        }

        if (request.AllowedFileTypes is not null)
        {
            var normalised = SettingsService.ParseFileTypes(string.Join(',', request.AllowedFileTypes));
            if (normalised.Length == 0)
                return Results.BadRequest("At least one file type must be allowed, or nothing could be uploaded.");

            await settings.SetAsync(SettingsService.AllowedFileTypesKey, string.Join(',', normalised));
        }

        if (request.MaxUploadSizeMb is { } maxUploadSizeMb)
        {
            if (maxUploadSizeMb < 0)
                return Results.BadRequest("The maximum upload size cannot be negative. Use 0 for no limit.");

            // Stored even when it is 0, rather than deleted to fall back to the default. They happen
            // to mean the same thing today, but "an admin chose no limit" and "nobody has touched
            // this" should not be indistinguishable in the database.
            await settings.SetAsync(SettingsService.MaxUploadSizeMbKey,
                maxUploadSizeMb.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return Results.Ok(await BuildContract(settings, context, provider));
    }

    private static IResult CheckPath(PathCheckRequest request)
        => Results.Ok(DirectoryProbe.Probe(request.Path));

    /// <summary>
    /// Proves the stored artwork key works, by searching for a title certain to exist.
    ///
    /// Goes through the provider rather than issuing its own request, so this tests the code path the
    /// picker actually uses — a bespoke request here could pass while the real one failed on a header
    /// or a parameter this one did not send. The provider already reduces every failure to a reason
    /// code with no exception detail.
    /// </summary>
    private static async Task<IResult> TestBoxArtKey(IBoxArtProvider provider)
    {
        var result = await provider.SearchAsync("Portal");
        return Results.Ok(new ProviderCredentialCheckResult
        {
            // "no-match" counts as working: the key was accepted and the provider answered. Only the
            // key itself is under test here, not its coverage of any particular title.
            Ok = result.Reason is "ok" or "no-match",
            Reason = result.Reason,
        });
    }

    /// <summary>
    /// Proves the stored IGDB credentials work, by searching for a title certain to exist.
    ///
    /// Through the provider rather than issuing its own request — the same reasoning as
    /// <see cref="TestBoxArtKey"/>, and it matters more here: authenticating with IGDB is two calls
    /// (a Twitch token grant, then the API request with two headers), and a bespoke check that only
    /// exercised the first would report "ok" for credentials the picker cannot actually use.
    ///
    /// This is also the one place an admin can tell "saved" from "works", which for a credential pair
    /// copied out of a web console is worth having.
    /// </summary>
    private static async Task<IResult> TestIgdbCredentials(IGameMetadataProvider provider)
    {
        var result = await provider.SearchAsync("Portal", page: 1, pageSize: 1);

        return Results.Ok(new ProviderCredentialCheckResult
        {
            Ok = result.Reason == "ok",
            Reason = result.Reason,
        });
    }
}
