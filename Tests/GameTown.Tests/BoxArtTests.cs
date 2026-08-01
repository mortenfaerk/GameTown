using API.Services;
using GameTown.Contracts.Games;
using System.Net;
using System.Net.Http.Json;

namespace GameTown.Tests;

/// <summary>
/// Box art: the stored form, and the rules on what may become one.
///
/// This is the first feature that makes the server fetch a URL a user chose, which is a class of
/// exposure GameTown did not previously have — so most of what is pinned here is refusal, not
/// success. The appliance sits *inside* a home LAN, so "fetch this address for me" is a more useful
/// primitive to an attacker here than it would be on a public host.
/// </summary>
public class BoxArtTests
{
    // Real headers, because that is what the sniffer reads. Twelve bytes minimum: the WebP check
    // needs to see "RIFF....WEBP" before it can decide.
    private static byte[] Png() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0xFF];

    private static byte[] Jpeg() =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF];

    private static async Task<Guid> AddGame(HttpClient client, string title = "Quake")
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent("Unzip and run"), "howTo" },
            { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes($"archive for {title}")), "file", "game.zip" },
        };

        var response = await client.PostAsync("/GTGames/Add", content);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AddGameResponse>())!.Id;
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid game, byte[] bytes, string name)
    {
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", name },
        };
        return await client.PostAsync($"/boxart/{game}/upload", form);
    }

    private static string[] MediaFiles(GameTownApp app)
    {
        var media = Path.Combine(app.DataDirectory, "media");
        return Directory.Exists(media) ? Directory.GetFiles(media) : [];
    }

    // ------------------------------------------------------------------ storing an upload

    [Fact]
    public async Task An_uploaded_image_is_stored_in_the_data_directory_and_shown_on_the_game()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client);

        var response = await Upload(client, game, Png(), "cover.png");
        response.EnsureSuccessStatusCode();

        var updated = await response.Content.ReadFromJsonAsync<GameContract>();
        Assert.StartsWith("/media/", updated!.BoxArtUrl);

        // In the DATA directory, not the application's wwwroot — an in-place upgrade replaces the
        // app folder, which is how the library's covers were silently emptied once before.
        var stored = Assert.Single(MediaFiles(app));
        Assert.Equal(Path.GetFileName(updated.BoxArtUrl), Path.GetFileName(stored));
    }

    /// <summary>
    /// The stored name comes from the sniffed content, never from the client. Two reasons: identical
    /// uploads would otherwise collide, and the extension decides the Content-Type this file is
    /// served back under from the API's own origin.
    /// </summary>
    [Fact]
    public async Task The_stored_name_ignores_the_uploaded_filename_and_follows_the_content()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client);

        // A PNG announced as a .jpg with a hostile name.
        var response = await Upload(client, game, Png(), "../../evil.jpg");
        response.EnsureSuccessStatusCode();

        var stored = Path.GetFileName(Assert.Single(MediaFiles(app)));
        Assert.EndsWith(".png", stored);
        Assert.DoesNotContain("evil", stored);
        Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(stored), out _));
    }

    /// <summary>
    /// Replacing a cover must not leave the old file behind. Every one is written under a fresh GUID,
    /// so without this each re-pick would quietly add another orphan to the data directory.
    /// </summary>
    [Fact]
    public async Task Replacing_the_cover_deletes_the_file_it_supersedes()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client);

        (await Upload(client, game, Png(), "a.png")).EnsureSuccessStatusCode();
        (await Upload(client, game, Jpeg(), "b.jpg")).EnsureSuccessStatusCode();

        var stored = Assert.Single(MediaFiles(app));
        Assert.EndsWith(".jpg", stored);
    }

    [Fact]
    public async Task Clearing_the_cover_removes_the_row_and_the_file()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client);

        (await Upload(client, game, Png(), "a.png")).EnsureSuccessStatusCode();

        var cleared = await client.DeleteAsync($"/boxart/{game}");
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);

        var fetched = await client.GetFromJsonAsync<GameContract>($"/GTGames/{game}");
        Assert.Null(fetched!.BoxArtUrl);
        Assert.Empty(MediaFiles(app));
    }

    /// <summary>Deleting the game takes its cover with it, or the file is orphaned forever.</summary>
    [Fact]
    public async Task Deleting_the_game_deletes_its_box_art()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client);

        (await Upload(client, game, Png(), "a.png")).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/GTGames/{game}")).EnsureSuccessStatusCode();

        Assert.Empty(MediaFiles(app));
    }

    // ------------------------------------------------------------------ what is refused

    /// <summary>
    /// Not an image, so nothing is stored. The important half is the second assertion: these files
    /// are served back as static content from the API's own origin, so storing an HTML document here
    /// would be stored XSS with the reach of the login page.
    /// </summary>
    [Fact]
    public async Task A_file_that_is_not_an_image_is_refused_and_nothing_is_written()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client);

        var html = System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        var response = await Upload(client, game, html, "cover.png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(MediaFiles(app));
    }

    /// <summary>
    /// SVG is an image to a person and a script host to a browser. It is deliberately absent from the
    /// allowlist, and this pins that decision so a future "why not SVG, it is an image" cannot
    /// quietly reintroduce it.
    /// </summary>
    [Fact]
    public async Task An_svg_is_refused_even_though_it_is_an_image()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client);

        var svg = System.Text.Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var response = await Upload(client, game, svg, "cover.svg");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(MediaFiles(app));
    }

    // ------------------------------------------------------------------ the outbound fetch

    /// <summary>
    /// The SSRF case, and the reason this feature needed a purpose-built fetcher. GameTown runs
    /// inside a home LAN, so a URL naming a private address turns "set a cover" into a request issued
    /// from behind the firewall — against a router's admin page, or a cloud instance's metadata
    /// endpoint at 169.254.169.254.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1/cover.png")]
    [InlineData("http://localhost/cover.png")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://192.168.1.1/cover.png")]
    [InlineData("http://10.0.0.5/cover.png")]
    [InlineData("http://172.16.4.4/cover.png")]
    [InlineData("http://[::1]/cover.png")]
    public async Task A_url_on_a_private_network_is_refused(string url)
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client);

        var response = await client.PostAsJsonAsync($"/boxart/{game}", new SetBoxArtRequest { Url = url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(MediaFiles(app));
    }

    /// <summary>
    /// file:// would be an arbitrary file read, and it is exactly the scheme someone reaches for when
    /// "paste an image link" does not do what they expected.
    /// </summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/cover.png")]
    [InlineData("not a url at all")]
    public async Task A_url_that_is_not_http_is_refused(string url)
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client);

        var response = await client.PostAsJsonAsync($"/boxart/{game}", new SetBoxArtRequest { Url = url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(MediaFiles(app));
    }

    // ------------------------------------------------------------------ the sniffer itself

    /// <summary>
    /// Pinned directly as well as through the endpoint, because this function decides the extension
    /// every stored file carries, and the endpoint tests could not distinguish "rejected for the
    /// right reason" from "rejected for another one".
    /// </summary>
    [Fact]
    public void The_sniffer_recognises_only_jpeg_png_and_webp()
    {
        Assert.Equal(".png", ImageFetcher.SniffExtension(Png()));
        Assert.Equal(".jpg", ImageFetcher.SniffExtension(Jpeg()));
        Assert.Equal(".webp", ImageFetcher.SniffExtension("RIFF    WEBPVP8 "u8.ToArray()));

        Assert.Null(ImageFetcher.SniffExtension("GIF89a______"u8.ToArray()));
        Assert.Null(ImageFetcher.SniffExtension("<svg xmlns=..."u8.ToArray()));
        Assert.Null(ImageFetcher.SniffExtension("%PDF-1.4____"u8.ToArray()));
        // Too short to identify anything — must not index past the end.
        Assert.Null(ImageFetcher.SniffExtension([0xFF, 0xD8]));
        Assert.Null(ImageFetcher.SniffExtension([]));
    }

    // ------------------------------------------------------------------ search and authorization

    /// <summary>
    /// With no artwork key stored, the search must report that as a state rather than an error —
    /// uploading a file and pasting a link both still work, and an error status would send the picker
    /// down its generic failure path instead of explaining what is missing.
    /// </summary>
    [Fact]
    public async Task Searching_without_a_configured_provider_reports_not_configured()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var result = await client.GetFromJsonAsync<BoxArtSearchResult>("/boxart/search?title=Portal");

        Assert.Equal("not-configured", result!.Reason);
        Assert.Empty(result.Candidates);
    }

    /// <summary>
    /// Every box-art route needs an account, and the refusal has to be a status code rather than a
    /// 302 to a login page — fetch follows a redirect and hands the caller a parsed web page.
    /// </summary>
    [Fact]
    public async Task Box_art_routes_require_an_account()
    {
        using var app = new GameTownApp();
        using var anonymous = app.CreateBrowser();
        var id = Guid.NewGuid();

        foreach (var response in new[]
        {
            await anonymous.GetAsync("/boxart/search?title=Portal"),
            await anonymous.PostAsJsonAsync($"/boxart/{id}", new SetBoxArtRequest { Url = "https://example.com/a.png" }),
            await anonymous.DeleteAsync($"/boxart/{id}"),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Setting_box_art_on_a_game_that_does_not_exist_is_a_404()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await Upload(client, Guid.NewGuid(), Png(), "a.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Box art wins over the RAWG image, and clearing it falls back rather than leaving the game with
    /// no cover at all. The precedence lives in the client, but the contract has to carry enough for
    /// it to be applied — which means both values present and independent.
    /// </summary>
    [Fact]
    public async Task Clearing_box_art_leaves_the_rest_of_the_game_untouched()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client, "Keeps Its Title");

        (await Upload(client, game, Png(), "a.png")).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/boxart/{game}")).EnsureSuccessStatusCode();

        var fetched = await client.GetFromJsonAsync<GameContract>($"/GTGames/{game}");
        Assert.Equal("Keeps Its Title", fetched!.Title);
        Assert.Equal("Unzip and run", fetched.HowTo);
        Assert.Null(fetched.BoxArtUrl);
    }
}
