using System.Net;
using System.Net.Http.Json;

namespace GameTown.Tests;

public class UploadTests
{
    private static MultipartFormDataContent Archive(string fileName, string title = "Test game")
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent("Unzip and run"), "howTo" },
            { new ByteArrayContent("not really an archive"u8.ToArray()), "file", fileName },
        };
        return content;
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

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = Directory.GetFiles(Path.Combine(app.DataDirectory, "games"));
        Assert.Single(stored);
        // The client-supplied name is never used to build the path: identical uploads would collide
        // and "../" would escape the directory entirely.
        Assert.DoesNotContain("game.zip", Path.GetFileName(stored[0]));
        Assert.EndsWith(".zip", stored[0]);
    }

    /// <summary>Changing the allowlist must take effect immediately, like every other setting.</summary>
    [Fact]
    public async Task Narrowing_the_allowlist_takes_effect_without_a_restart()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var allowed = await client.PostAsync("/GTGames/Add", Archive("first.zip"));
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);

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

    private sealed record GameDto(Guid Id, string Title, string HowTo, double Size);
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
