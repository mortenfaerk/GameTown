using API.Services;
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
        // Read from the probe rather than a second copy of the list here: this test used to carry its
        // own, and adding "unc-not-supported" to the real set left it asserting against a stale one.
        foreach (var path in new[] { app.DataDirectory, "relative/path", @"\\nas\games", "/proc/nope/nope" })
        {
            var response = await client.PostAsJsonAsync("/settings/check-path", new { path });
            var result = await response.Content.ReadFromJsonAsync<PathCheck>();
            Assert.Contains(result!.Reason, DirectoryProbe.Reasons);
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

    private sealed record PathCheck(
        bool Exists, bool Writable, string Reason, long? FreeBytes, string? FileSystem);

    private sealed record KeyCheck(bool Ok, string Reason);
}

/// <summary>
/// The archive-directory check, which is the difference between "GameTown told me at setup" and "the
/// first upload failed at 90%". It writes a file and deletes it rather than reading permission bits:
/// the service runs as its own user, and on a CIFS mount the effective rights are the mount
/// credentials', not the caller's — neither mode bits nor ownership predict the answer.
/// </summary>
public class DirectoryProbeTests
{
    private static string TempDirectory()
        => Path.Combine(Path.GetTempPath(), "gametown-probe-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_writable_directory_reports_ok_with_the_filesystem_it_sits_on()
    {
        var path = TempDirectory();
        Directory.CreateDirectory(path);
        try
        {
            var result = DirectoryProbe.Probe(path);

            Assert.True(result.Writable);
            Assert.Equal("ok", result.Reason);
            // Reported so an operator can see whether the share they meant to use is really mounted:
            // an unmounted mountpoint looks writable and is the local disk.
            Assert.False(string.IsNullOrWhiteSpace(result.FileSystem));
            Assert.NotNull(result.FreeBytes);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void A_directory_that_does_not_exist_yet_is_created()
    {
        var path = TempDirectory();
        try
        {
            var result = DirectoryProbe.Probe(path);

            Assert.True(result.Exists);
            Assert.True(result.Writable);
            Assert.True(Directory.Exists(path));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void The_probe_file_does_not_survive_the_check()
    {
        var path = TempDirectory();
        Directory.CreateDirectory(path);
        try
        {
            DirectoryProbe.Probe(path);

            Assert.Empty(Directory.GetFileSystemEntries(path));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_path_that_is_not_absolute_is_rejected(string? path)
        => Assert.Equal("not-absolute", DirectoryProbe.Probe(path).Reason);

    /// <summary>
    /// A share address gets its own code rather than "not absolute", which is technically true and
    /// useless: it tells someone who just typed their NAS address to add a leading slash. GameTown
    /// runs unprivileged and cannot mount anything, so the share has to become a mountpoint first —
    /// which is what smb-mount.sh does, and what the message points at.
    /// </summary>
    [Theory]
    [InlineData(@"\\nas\games")]
    [InlineData(@"\\192.168.1.10\media\games")]
    [InlineData("smb://nas/games")]
    [InlineData("cifs://nas/games")]
    public void A_network_share_address_is_named_as_such(string share)
    {
        var result = DirectoryProbe.Probe(share);

        Assert.Equal("unc-not-supported", result.Reason);
        Assert.False(result.Writable);
    }

    /// <summary>
    /// "//foo" is a legal absolute POSIX path, so it must not be swept up by the share detection —
    /// rejecting a directory that works is worse than the slightly narrower guess.
    /// </summary>
    [Fact]
    public void A_posix_path_beginning_with_two_slashes_is_not_treated_as_a_share()
    {
        var path = "//tmp/gametown-probe-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.NotEqual("unc-not-supported", DirectoryProbe.Probe(path).Reason);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void A_directory_the_service_cannot_write_to_reports_permission_denied()
    {
        // Root ignores the mode bits this test relies on, so there is nothing to assert as root.
        // Skipped rather than faked: a test that quietly proves nothing is worse than one that says so.
        // Windows has no equivalent of the mode being set here, and the appliance is Linux-only.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;

        var path = TempDirectory();
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = DirectoryProbe.Probe(path);

            Assert.False(result.Writable);
            Assert.Equal("permission-denied", result.Reason);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void Every_answer_is_one_of_the_published_reason_codes()
    {
        foreach (var path in new[] { Path.GetTempPath(), "relative", @"\\nas\games", "/proc/nope/nope" })
            Assert.Contains(DirectoryProbe.Probe(path).Reason, DirectoryProbe.Reasons);
    }
}

/// <summary>
/// The first-run wizard's half of the same check. It matters more here than on the settings page:
/// /setup answers 404 the moment an administrator exists, so an archive path accepted on the way out
/// would leave the operator with a working login, a directory GameTown cannot write to, and no
/// wizard left to fix it in.
/// </summary>
public class SetupPathTests
{
    [Fact]
    public async Task An_unusable_archive_path_is_refused_and_leaves_the_wizard_open()
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var response = await PostSetupAsync(client, "/proc/gametown/nope");

        // 200 is the form re-rendered with an error; success would have been a 302 to /login.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("directory", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // The wizard must still be reachable — the whole point of checking before creating the admin.
        var stillOpen = await client.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.OK, stillOpen.StatusCode);
    }

    [Fact]
    public async Task A_share_address_is_refused_with_advice_rather_than_a_path_complaint()
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var response = await PostSetupAsync(client, @"\\nas\games");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("mount", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_writable_archive_path_completes_setup_and_is_what_the_service_uses()
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();
        var archives = Path.Combine(Path.GetTempPath(), "gametown-setup-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var response = await PostSetupAsync(client, archives);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            // Signed in through the account the wizard just made, because the point is that the path
            // it accepted is the one the running service now stores against.
            var signedIn = app.CreateBrowser();
            var login = await signedIn.PostAsJsonAsync("/auth/login",
                new { username = "setupadmin", password = "setuppassword" });
            login.EnsureSuccessStatusCode();

            var settings = await signedIn.GetFromJsonAsync<SettingsDto>("/settings");
            Assert.Equal(archives, settings!.GameFilesPath);
        }
        finally { if (Directory.Exists(archives)) Directory.Delete(archives, recursive: true); }
    }

    private static async Task<HttpResponseMessage> PostSetupAsync(HttpClient client, string gameFilesPath)
    {
        var token = GameTownApp.ExtractAntiforgeryToken(await client.GetStringAsync("/setup"));

        return await client.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Username"] = "setupadmin",
            ["Password"] = "setuppassword",
            ["ConfirmPassword"] = "setuppassword",
            ["GameFilesPath"] = gameFilesPath,
        }));
    }

    private sealed record SettingsDto(string GameFilesPath);
}
