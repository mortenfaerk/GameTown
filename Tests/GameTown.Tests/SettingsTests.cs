using System.Net;
using System.Net.Http.Json;

namespace GameTown.Tests;

public class SettingsTests
{
    /// <summary>
    /// The check that justifies the whole Phase 3 refactor. RAWGService used to take its key as a
    /// constructor argument resolved once at startup, so the settings page would have saved
    /// successfully and changed nothing until a restart.
    ///
    /// The app boots here with no key configured anywhere, so a "not-configured" answer turning into
    /// anything else can only mean the running service read the new value.
    /// </summary>
    [Fact]
    public async Task A_saved_rawg_key_is_visible_to_the_running_service_without_a_restart()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var before = await client.PostAsync("/settings/test-rawg-key", null);
        var beforeResult = await before.Content.ReadFromJsonAsync<KeyCheck>();
        Assert.Equal("not-configured", beforeResult!.Reason);

        await client.PatchAsJsonAsync("/settings", new { rawgApiKey = "some-test-key-1234" });

        var after = await client.PostAsync("/settings/test-rawg-key", null);
        var afterResult = await after.Content.ReadFromJsonAsync<KeyCheck>();

        // Anything other than "not-configured" proves the key was read at call time. Which of
        // "rejected" or "unreachable" comes back depends on whether the test machine has internet,
        // so both are accepted — asserting on one would make this fail offline for the wrong reason.
        Assert.NotEqual("not-configured", afterResult!.Reason);
    }

    [Fact]
    public async Task Defaults_apply_when_nothing_is_configured()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var settings = await client.GetFromJsonAsync<SettingsDto>("/settings");

        Assert.False(settings!.RawgApiKeyIsSet);
        Assert.Contains(".zip", settings.AllowedFileTypes);
        Assert.StartsWith(app.DataDirectory, settings.GameFilesPath);
        Assert.StartsWith(app.DataDirectory, settings.MediaDirectory);
    }

    /// <summary>
    /// A secret must never be round-tripped through the browser. It is returned masked with a flag,
    /// which is what makes a blank submission mean "unchanged" rather than "clear".
    /// </summary>
    [Fact]
    public async Task The_rawg_key_is_returned_masked_and_never_in_full()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        await client.PatchAsJsonAsync("/settings", new { rawgApiKey = "supersecretkey9876" });
        var settings = await client.GetFromJsonAsync<SettingsDto>("/settings");

        Assert.True(settings!.RawgApiKeyIsSet);
        Assert.DoesNotContain("supersecretkey", settings.RawgApiKeyMasked ?? "");
        Assert.EndsWith("9876", settings.RawgApiKeyMasked);
    }

    [Fact]
    public async Task A_blank_key_leaves_the_stored_one_alone()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        await client.PatchAsJsonAsync("/settings", new { rawgApiKey = "keepthiskey1234" });
        await client.PatchAsJsonAsync("/settings", new { rawgApiKey = "" });
        var settings = await client.GetFromJsonAsync<SettingsDto>("/settings");

        Assert.True(settings!.RawgApiKeyIsSet);
    }

    [Fact]
    public async Task Clearing_the_key_is_an_explicit_action()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        await client.PatchAsJsonAsync("/settings", new { rawgApiKey = "removethiskey" });
        await client.PatchAsJsonAsync("/settings", new { clearRawgApiKey = true });
        var settings = await client.GetFromJsonAsync<SettingsDto>("/settings");

        Assert.False(settings!.RawgApiKeyIsSet);
    }

    [Fact]
    public async Task File_types_are_normalised()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await client.PatchAsJsonAsync("/settings",
            new { allowedFileTypes = new[] { "ZIP", ".7z", "zip", " .Rar " } });
        var settings = await response.Content.ReadFromJsonAsync<SettingsDto>();

        Assert.Equal([".zip", ".7z", ".rar"], settings!.AllowedFileTypes);
    }

    /// <summary>An empty allowlist would accept nothing, making uploading impossible.</summary>
    [Fact]
    public async Task An_empty_file_type_list_is_rejected()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await client.PatchAsJsonAsync("/settings", new { allowedFileTypes = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// check-path reports on arbitrary server paths, so it must never echo exception text — that
    /// would disclose directory structure from an endpoint whose whole job is probing the filesystem.
    /// </summary>
    [Fact]
    public async Task Path_checks_answer_with_fixed_reason_codes()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var known = new[] { "ok", "not-absolute", "permission-denied", "not-found", "io-error", "invalid" };

        foreach (var path in new[] { app.DataDirectory, "relative/path", "/proc/nope/nope" })
        {
            var response = await client.PostAsJsonAsync("/settings/check-path", new { path });
            var result = await response.Content.ReadFromJsonAsync<PathCheck>();
            Assert.Contains(result!.Reason, known);
        }
    }

    [Fact]
    public async Task A_writable_directory_reports_writable()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await client.PostAsJsonAsync("/settings/check-path", new { path = app.DataDirectory });
        var result = await response.Content.ReadFromJsonAsync<PathCheck>();

        Assert.True(result!.Exists);
        Assert.True(result.Writable);
    }

    private sealed record SettingsDto(
        string GameFilesPath, string MediaDirectory, string DataDirectory,
        bool RawgApiKeyIsSet, string? RawgApiKeyMasked, List<string> AllowedFileTypes);

    private sealed record PathCheck(bool Exists, bool Writable, string Reason, long? FreeBytes);

    private sealed record KeyCheck(bool Ok, string Reason);
}
