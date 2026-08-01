using GameTown.Contracts.Games;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;

namespace GameTown.Tests;

public class UploadTests
{
    private static MultipartFormDataContent Archive(string fileName, string title = "Test game")
        => Archive(fileName, "not really an archive"u8.ToArray(), title);

    private static MultipartFormDataContent Archive(string fileName, byte[] bytes, string title = "Test game")
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent("Unzip and run"), "howTo" },
            { new ByteArrayContent(bytes), "file", fileName },
        };
        return content;
    }

    /// <summary>Everything in the archive directory, including any leftover ".part" file.</summary>
    private static string[] ArchiveDirectory(GameTownApp app)
    {
        var games = Path.Combine(app.DataDirectory, "games");
        return Directory.Exists(games) ? Directory.GetFiles(games) : [];
    }

    /// <summary>
    /// The allowlist is a server-side control, not a hint to the file picker. The SPA also filters
    /// the picker, but anything can POST here directly.
    /// </summary>
    [Fact]
    public async Task An_extension_outside_the_allowlist_is_refused()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await client.PostAsync("/GTGames/Add", Archive("payload.exe"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_allowed_extension_is_accepted_and_stored()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await client.PostAsync("/GTGames/Add", Archive("game.zip"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = Directory.GetFiles(Path.Combine(app.DataDirectory, "games"));
        Assert.Single(stored);
        // The client-supplied name is never used to build the path: identical uploads would collide
        // and "../" would escape the directory entirely.
        Assert.DoesNotContain("game.zip", Path.GetFileName(stored[0]));
        Assert.EndsWith(".zip", stored[0]);
    }

    /// <summary>
    /// The response has to carry the new game's id, and that id has to be the one a caller can
    /// immediately address.
    ///
    /// This is what turns "the archive is uploaded" into "the game can now be described": tags and
    /// box art are set through their own endpoints, and with the 204 this used to answer there was
    /// nothing to point them at. An id that came back but did not resolve would be worse than none,
    /// because the failure would land on the follow-up call instead of here.
    /// </summary>
    [Fact]
    public async Task A_successful_upload_answers_with_the_new_games_id()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await client.PostAsync("/GTGames/Add", Archive("game.zip", title: "Findable"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<AddGameResponse>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.Id);

        var fetched = await client.GetFromJsonAsync<GameContract>($"/GTGames/{created.Id}");
        Assert.Equal("Findable", fetched!.Title);
    }

    /// <summary>Changing the allowlist must take effect immediately, like every other setting.</summary>
    [Fact]
    public async Task Narrowing_the_allowlist_takes_effect_without_a_restart()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var allowed = await client.PostAsync("/GTGames/Add", Archive("first.zip"));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);

        await client.PatchAsJsonAsync("/settings", new { allowedFileTypes = new[] { ".7z" } });

        var refused = await client.PostAsync("/GTGames/Add", Archive("second.zip"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Uploading_requires_an_account()
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var response = await client.PostAsync("/GTGames/Add", Archive("game.zip"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_uploaded_game_appears_in_the_anonymous_library()
    {
        using var app = new GameTownApp();
        using var admin = await app.SignInAsAdminAsync();
        await admin.PostAsync("/GTGames/Add", Archive("game.zip", "Findable"));

        using var anonymous = app.CreateBrowser();
        var games = await anonymous.GetFromJsonAsync<List<GameDto>>("/GTGames/getPaged/1/10");

        Assert.Contains(games!, g => g.Title == "Findable");
    }

    /// <summary>
    /// The archive is streamed straight to its final location as the body arrives, so nothing may be
    /// left behind when the upload is refused. The old buffered handler could not fail this — it
    /// wrote the file only after the whole request had been accepted — but streaming means the bytes
    /// are already on disk by the time some of these checks run.
    /// </summary>
    [Fact]
    public async Task A_rejected_file_type_leaves_nothing_in_the_archive_directory()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await client.PostAsync("/GTGames/Add", Archive("payload.exe"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(ArchiveDirectory(app));
    }

    /// <summary>
    /// A missing title cannot be caught before the file is written: it arrives in the same body. So
    /// the archive really is on disk when the request is rejected, and the handler has to remove it
    /// or every failed upload permanently consumes the disk.
    /// </summary>
    [Fact]
    public async Task A_missing_title_is_refused_and_the_stored_archive_is_removed()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var content = new MultipartFormDataContent
        {
            { new StringContent("Unzip and run"), "howTo" },
            { new ByteArrayContent("not really an archive"u8.ToArray()), "file", "game.zip" },
        };

        var response = await client.PostAsync("/GTGames/Add", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(ArchiveDirectory(app));
    }

    /// <summary>
    /// Nothing in the multipart format guarantees field order, and the streaming reader must not
    /// depend on one. upload.js sends the text fields first today; if that ever changes, or another
    /// client sends the file first, the upload still has to work.
    /// </summary>
    [Fact]
    public async Task The_file_may_arrive_before_the_text_fields()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent("not really an archive"u8.ToArray()), "file", "game.zip" },
            { new StringContent("Backwards"), "title" },
            { new StringContent("Unzip and run"), "howTo" },
        };

        var response = await client.PostAsync("/GTGames/Add", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Backwards", app.QueryScalar(@"SELECT ""Title"" FROM ""GameTownGame""").Trim());
    }

    [Fact]
    public async Task The_recorded_size_is_the_number_of_bytes_actually_received()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var payload = new byte[3 * 1024 * 1024];
        var response = await client.PostAsync("/GTGames/Add", Archive("game.zip", payload, "Three megabytes"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = Assert.Single(ArchiveDirectory(app));
        Assert.Equal(payload.Length, new FileInfo(stored).Length);
        Assert.Equal(3.0, double.Parse(app.QueryScalar(@"SELECT ""Size"" FROM ""GameTownGame""").Trim(),
            System.Globalization.CultureInfo.InvariantCulture), precision: 3);
    }

    /// <summary>
    /// The ceiling is an admin setting, so like every other setting it has to take effect on the
    /// running process rather than at the next restart.
    /// </summary>
    [Fact]
    public async Task An_upload_over_the_configured_ceiling_is_refused_with_413()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        await client.PatchAsJsonAsync("/settings", new { maxUploadSizeMb = 1 });

        var response = await client.PostAsync("/GTGames/Add",
            Archive("huge.zip", new byte[2 * 1024 * 1024]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        // Partially written before the limit was hit, so this is the check that the ".part" file is
        // cleaned up rather than left as a 1 MB orphan.
        Assert.Empty(ArchiveDirectory(app));
    }

    [Fact]
    public async Task An_upload_within_the_configured_ceiling_is_accepted()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        await client.PatchAsJsonAsync("/settings", new { maxUploadSizeMb = 4 });

        var response = await client.PostAsync("/GTGames/Add",
            Archive("fine.zip", new byte[2 * 1024 * 1024]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(ArchiveDirectory(app));
    }

    /// <summary>Zero is a real value meaning "no ceiling", not "reject everything".</summary>
    [Fact]
    public async Task A_ceiling_of_zero_means_unlimited()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        await client.PatchAsJsonAsync("/settings", new { maxUploadSizeMb = 0 });

        var response = await client.PostAsync("/GTGames/Add", Archive("game.zip"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// The upload form asks for these so it can refuse a bad file before spending twenty minutes
    /// sending it. It is a convenience — both rules are enforced server-side regardless — but a
    /// wrong answer here means the browser blocks uploads the server would have taken.
    /// </summary>
    [Fact]
    public async Task Upload_limits_are_reported_to_contributors()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        await client.PatchAsJsonAsync("/settings", new { maxUploadSizeMb = 512, allowedFileTypes = new[] { ".zip" } });

        var limits = await client.GetFromJsonAsync<UploadLimitsDto>("/GTGames/upload-limits");

        Assert.Equal(512, limits!.MaxUploadSizeMb);
        Assert.Equal([".zip"], limits.AllowedFileTypes);
    }

    /// <summary>
    /// A literal path segment competing with "/GTGames/{id}". If routing ever preferred the
    /// parameter, this would be parsed as a game id and answer 400 — or, under SPA-fallback hosting,
    /// come back as 200 text/html.
    /// </summary>
    [Fact]
    public async Task The_upload_limits_route_is_not_swallowed_by_the_game_id_route()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await client.GetAsync("/GTGames/upload-limits");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Upload_limits_require_an_account()
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync("/GTGames/upload-limits");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record GameDto(Guid Id, string Title, string HowTo, double Size);

    private sealed record UploadLimitsDto(long MaxUploadSizeMb, string[] AllowedFileTypes);
}

/// <summary>
/// The guard against a retry that should never have happened: a long upload outlives a reverse
/// proxy's read timeout, the browser reports a network error, the server completes anyway, and the
/// contributor uploads the same archive a second time.
/// </summary>
public class UploadDeduplicationTests
{
    private static MultipartFormDataContent Archive(byte[] bytes, string title, string fileName = "game.zip")
        => new()
        {
            { new StringContent(title), "title" },
            { new StringContent("Unzip and run"), "howTo" },
            { new ByteArrayContent(bytes), "file", fileName },
        };

    private static string[] StoredArchives(GameTownApp app)
    {
        var games = Path.Combine(app.DataDirectory, "games");
        return Directory.Exists(games) ? Directory.GetFiles(games) : [];
    }

    [Fact]
    public async Task Re_uploading_the_same_archive_is_refused_as_a_conflict()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var payload = "the very same bytes"u8.ToArray();

        var first = await client.PostAsync("/GTGames/Add", Archive(payload, "Doom"));
        var second = await client.PostAsync("/GTGames/Add", Archive(payload, "Doom"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // The message names the existing entry, because the whole point is telling the contributor
        // that their "failed" upload in fact succeeded.
        Assert.Contains("Doom", await second.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The archive is already on disk by the time the duplicate is detected — it has to be, since
    /// the hash is not known until the last byte arrives. Leaving it would mean every retry
    /// permanently consumed another copy's worth of disk, which is most of what this guard is for.
    /// </summary>
    [Fact]
    public async Task A_refused_duplicate_leaves_only_the_original_on_disk()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var payload = "the very same bytes"u8.ToArray();
        await client.PostAsync("/GTGames/Add", Archive(payload, "Doom"));
        await client.PostAsync("/GTGames/Add", Archive(payload, "Doom"));

        Assert.Single(StoredArchives(app));
        Assert.Equal("1", app.QueryScalar(@"SELECT COUNT(*) FROM ""GameTownGame""").Trim());
    }

    /// <summary>A retry does not have to reuse the title — the archive is the identity.</summary>
    [Fact]
    public async Task The_same_archive_under_a_different_title_is_still_a_duplicate()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var payload = "the very same bytes"u8.ToArray();
        await client.PostAsync("/GTGames/Add", Archive(payload, "Doom"));
        var second = await client.PostAsync("/GTGames/Add", Archive(payload, "Doom II"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Different_archives_with_the_same_title_are_both_accepted()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var first = await client.PostAsync("/GTGames/Add", Archive("one"u8.ToArray(), "Doom"));
        var second = await client.PostAsync("/GTGames/Add", Archive("two"u8.ToArray(), "Doom"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("2", app.QueryScalar(@"SELECT COUNT(*) FROM ""GameTownGame""").Trim());
    }

    [Fact]
    public async Task The_hash_is_recorded_against_the_game()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        await client.PostAsync("/GTGames/Add", Archive("hash me"u8.ToArray(), "Hashed"));

        var stored = app.QueryScalar(@"SELECT ""ArchiveSha256"" FROM ""GameTownGame""").Trim();
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData("hash me"u8.ToArray()));

        Assert.Equal(expected, stored);
    }

    /// <summary>
    /// Games that predate migration 003 carry no hash. They must not all match each other on NULL,
    /// which would make the first pre-existing game block every subsequent upload.
    /// </summary>
    [Fact]
    public async Task Games_with_no_recorded_hash_do_not_match_each_other()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        await client.PostAsync("/GTGames/Add", Archive("legacy"u8.ToArray(), "Legacy"));
        app.QueryScalar(@"UPDATE ""GameTownGame"" SET ""ArchiveSha256"" = NULL");

        var response = await client.PostAsync("/GTGames/Add", Archive("something else"u8.ToArray(), "Modern"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}

/// <summary>
/// ArchiveUpload deletes its own ".part" file on every path it can reach. The one it cannot reach is
/// the process no longer existing — a systemd restart mid-upload, an OOM kill, a power cut — which
/// is what the startup sweep is for.
/// </summary>
public class AbandonedUploadTests
{
    [Fact]
    public async Task Part_files_from_a_previous_run_are_removed_at_startup()
    {
        using var app = new GameTownApp();

        // Boot once so the archive directory exists, then plant what a killed process would leave.
        using (var client = await app.SignInAsAdminAsync())
        {
            var games = Path.Combine(app.DataDirectory, "games");
            Directory.CreateDirectory(games);
            await File.WriteAllBytesAsync(Path.Combine(games, $"{Guid.NewGuid()}.zip.part"), new byte[4096]);
            await File.WriteAllBytesAsync(Path.Combine(games, $"{Guid.NewGuid()}.zip.part"), new byte[4096]);
            await File.WriteAllTextAsync(Path.Combine(games, $"{Guid.NewGuid()}.zip"), "a real archive");
        }

        // A second application over the same data directory is the restart.
        using var restarted = new RestartedApp(app.DataDirectory);
        using var _ = restarted.CreateClient();

        var remaining = Directory.GetFiles(Path.Combine(app.DataDirectory, "games"));
        Assert.Single(remaining);
        Assert.EndsWith(".zip", remaining[0]);
    }

    /// <summary>
    /// Boots a second application instance against an existing data directory, which is what a
    /// service restart looks like from the filesystem's point of view.
    /// </summary>
    private sealed class RestartedApp(string dataDirectory)
        : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        protected override Microsoft.Extensions.Hosting.IHost CreateHost(
            Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] =
                        $"Data Source={Path.Combine(dataDirectory, "gametown.db")}",
                }));

            return base.CreateHost(builder);
        }
    }
}

public class SetupTests
{
    [Fact]
    public async Task Setup_is_reachable_on_a_fresh_install()
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync("/setup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The gate is "does an admin exist", evaluated per request. A flag captured at startup would
    /// stay open for the life of the process and leave this creating administrators forever.
    /// </summary>
    [Fact]
    public async Task Setup_closes_once_an_administrator_exists()
    {
        using var app = new GameTownApp();
        await app.CreateAdminAsync("owner", "ownerpassword");

        using var client = app.CreateBrowser();
        var get = await client.GetAsync("/setup");
        var post = await client.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "sneaky", ["Password"] = "sneakypassword", ["ConfirmPassword"] = "sneakypassword",
        }));

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        // The POST is rejected before it can do anything, but by which of the two guards is not the
        // point and is not worth pinning: antiforgery fires first (400) because the token can no
        // longer be fetched from a page that now 404s, and the admin-exists gate would answer 404.
        // What actually matters is that no second account appears, so assert that.
        Assert.True(post.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"setup POST returned {(int)post.StatusCode}");
        Assert.Equal("1", app.QueryScalar(@"SELECT COUNT(*) FROM ""GameTownUsers""").Trim());
    }

    /// <summary>
    /// Guards the _ViewImports.cshtml that activates tag helpers. Without it the form emits no
    /// antiforgery token while Razor Pages still validates one, so every submission fails with a
    /// bare 400 and no explanation.
    /// </summary>
    [Fact]
    public async Task The_setup_form_carries_an_antiforgery_token()
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var html = await client.GetStringAsync("/setup");

        Assert.Contains("__RequestVerificationToken", html);
    }

    [Fact]
    public async Task No_credentials_are_seeded()
    {
        using var app = new GameTownApp();

        var users = app.QueryScalar(@"SELECT COUNT(*) FROM ""GameTownUsers""").Trim();
        var roles = app.QueryScalar(@"SELECT COUNT(*) FROM ""GameTownRoles""").Trim();

        Assert.Equal("0", users);
        Assert.Equal("2", roles);
    }
}
