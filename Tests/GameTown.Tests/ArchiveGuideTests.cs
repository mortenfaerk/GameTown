using GameTown.Contracts.Games;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace GameTown.Tests;

/// <summary>
/// Writing the instructions into the archive, end to end through the real endpoints.
///
/// <see cref="ZipGuideWriterTests"/> covers the ZIP surgery in isolation. What is pinned here is the
/// wiring around it: that the file written is the one the game actually has, that editing the
/// instructions does not leave a stale copy inside the download, and that a format which cannot carry
/// a guide says so rather than failing obscurely.
/// </summary>
public class ArchiveGuideTests
{
    private const string GuideName = "GameTownGuide.txt";

    /// <summary>A real ZIP, since the endpoint will be doing real ZIP surgery on it.</summary>
    private static byte[] ZipBytes(string marker)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("game.exe");
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes($"pretend executable {marker}"));
        }
        return buffer.ToArray();
    }

    private static async Task<Guid> AddGame(
        HttpClient client, string title, byte[] archive, string fileName = "game.zip",
        string howTo = "Unzip and run game.exe")
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent(howTo), "howTo" },
            { new ByteArrayContent(archive), "file", fileName },
        };

        var response = await client.PostAsync("/GTGames/Add", content);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AddGameResponse>())!.Id;
    }

    private static async Task<HttpResponseMessage> SetGuide(HttpClient client, Guid id, bool baked)
        => await client.PutAsJsonAsync($"/guide/{id}", new SetGuideRequest { Baked = baked });

    /// <summary>The stored archive — there is exactly one game per test here.</summary>
    private static string StoredArchive(GameTownApp app)
        => Directory.GetFiles(Path.Combine(app.DataDirectory, "games")).Single();

    private static string? ReadGuide(GameTownApp app)
    {
        using var zip = ZipFile.OpenRead(StoredArchive(app));
        var entry = zip.GetEntry(GuideName);
        if (entry is null) return null;

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ------------------------------------------------------------------ the happy path

    [Fact]
    public async Task Turning_the_toggle_on_writes_the_instructions_into_the_archive()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client, "Quake", ZipBytes("q"), howTo: "Run QUAKE.EXE with -nocdaudio");

        var response = await SetGuide(client, game, baked: true);
        response.EnsureSuccessStatusCode();

        var guide = ReadGuide(app);
        Assert.NotNull(guide);
        Assert.Contains("Quake", guide);
        Assert.Contains("Run QUAKE.EXE with -nocdaudio", guide);

        // The response carries the updated game, so the UI does not need a second call to find out
        // whether the toggle took.
        var updated = await response.Content.ReadFromJsonAsync<GameContract>();
        Assert.True(updated!.GuideBaked);
    }

    /// <summary>
    /// The archive still extracts, and the game itself is untouched. Adding a text file to somebody's
    /// upload is only acceptable if the upload still works afterwards.
    /// </summary>
    [Fact]
    public async Task The_rest_of_the_archive_still_reads_afterwards()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client, "Quake", ZipBytes("q"));

        (await SetGuide(client, game, baked: true)).EnsureSuccessStatusCode();

        using var zip = ZipFile.OpenRead(StoredArchive(app));
        using var reader = new StreamReader(zip.GetEntry("game.exe")!.Open());

        Assert.Equal("pretend executable q", reader.ReadToEnd());
    }

    [Fact]
    public async Task Turning_the_toggle_off_removes_it_again()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client, "Quake", ZipBytes("q"));

        (await SetGuide(client, game, baked: true)).EnsureSuccessStatusCode();
        var response = await SetGuide(client, game, baked: false);
        response.EnsureSuccessStatusCode();

        Assert.Null(ReadGuide(app));
        Assert.False((await response.Content.ReadFromJsonAsync<GameContract>())!.GuideBaked);
    }

    /// <summary>
    /// The whole point of the flag: a game's instructions and the copy inside its download must not
    /// drift apart. A stale copy is worse than none, because the person reading it has no way of
    /// telling that it is out of date.
    /// </summary>
    [Fact]
    public async Task Editing_the_instructions_rewrites_the_copy_in_the_archive()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client, "Quake", ZipBytes("q"), howTo: "Original instructions");

        (await SetGuide(client, game, baked: true)).EnsureSuccessStatusCode();
        Assert.Contains("Original instructions", ReadGuide(app));

        var patched = await client.PatchAsJsonAsync("/GTGames/update", new
        {
            id = game.ToString(),
            howTo = "Corrected instructions",
        });
        patched.EnsureSuccessStatusCode();

        var guide = ReadGuide(app);
        Assert.Contains("Corrected instructions", guide);
        Assert.DoesNotContain("Original instructions", guide);
    }

    /// <summary>And a game without one is left entirely alone, archive timestamp and all.</summary>
    [Fact]
    public async Task Editing_a_game_with_no_guide_does_not_touch_its_archive()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client, "Quake", ZipBytes("q"));

        var before = File.ReadAllBytes(StoredArchive(app));

        var patched = await client.PatchAsJsonAsync("/GTGames/update", new
        {
            id = game.ToString(),
            howTo = "Something else entirely",
        });
        patched.EnsureSuccessStatusCode();

        Assert.Equal(before, File.ReadAllBytes(StoredArchive(app)));
    }

    /// <summary>
    /// Applying the same state twice must not write anything the second time — otherwise every save
    /// of an already-guided game would grow the archive.
    /// </summary>
    [Fact]
    public async Task Setting_the_same_state_twice_is_idempotent()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client, "Quake", ZipBytes("q"));

        (await SetGuide(client, game, baked: false)).EnsureSuccessStatusCode();
        var untouched = new FileInfo(StoredArchive(app)).Length;

        (await SetGuide(client, game, baked: false)).EnsureSuccessStatusCode();
        Assert.Equal(untouched, new FileInfo(StoredArchive(app)).Length);

        (await SetGuide(client, game, baked: true)).EnsureSuccessStatusCode();
        using var zip = ZipFile.OpenRead(StoredArchive(app));
        Assert.Single(zip.Entries.Where(e => e.FullName == GuideName));
    }

    // ------------------------------------------------------------------ what the contract reports

    [Fact]
    public async Task A_zip_reports_that_it_can_carry_a_guide_and_a_rar_does_not()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var zipGame = await AddGame(client, "Zipped", ZipBytes("z"), "game.zip");
        // Not a real RAR — the endpoint only ever looks at the extension to decide this.
        var rarGame = await AddGame(client, "Rarred", "not a real rar"u8.ToArray(), "game.rar");

        var zipped = await client.GetFromJsonAsync<GameContract>($"/GTGames/{zipGame}");
        var rarred = await client.GetFromJsonAsync<GameContract>($"/GTGames/{rarGame}");

        Assert.True(zipped!.CanBakeGuide);
        Assert.False(rarred!.CanBakeGuide);
        Assert.False(zipped.GuideBaked);
    }

    /// <summary>
    /// A format that cannot carry a guide is refused with a reason, not a 500 and not a silent
    /// success. The UI shows the toggle disabled, but anything can call this endpoint directly.
    /// </summary>
    [Fact]
    public async Task A_non_zip_archive_is_refused_with_an_explanation()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client, "Rarred", "not a real rar"u8.ToArray(), "game.rar");

        var response = await SetGuide(client, game, baked: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ZIP", await response.Content.ReadAsStringAsync());

        var unchanged = await client.GetFromJsonAsync<GameContract>($"/GTGames/{game}");
        Assert.False(unchanged!.GuideBaked);
    }

    /// <summary>
    /// Named .zip but not one — an interrupted or renamed upload. The archive must come back
    /// untouched, and the flag must not be set for a file that carries nothing.
    /// </summary>
    [Fact]
    public async Task An_archive_that_is_not_really_a_zip_is_refused_and_left_alone()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client, "Broken", "this is not a zip at all"u8.ToArray(), "game.zip");

        var before = File.ReadAllBytes(StoredArchive(app));
        var response = await SetGuide(client, game, baked: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(StoredArchive(app)));
        Assert.False((await client.GetFromJsonAsync<GameContract>($"/GTGames/{game}"))!.GuideBaked);
    }

    // ------------------------------------------------------------------ authorization and errors

    [Fact]
    public async Task Writing_a_guide_requires_a_contributor()
    {
        using var app = new GameTownApp();
        using var anonymous = app.CreateBrowser();

        var response = await SetGuide(anonymous, Guid.NewGuid(), baked: true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_game_is_a_404()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await SetGuide(client, Guid.NewGuid(), baked: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Deleting the game still works once its archive has been modified — the guide is inside the
    /// file, so nothing about the stored path changed, but this is cheap to be sure of.
    /// </summary>
    [Fact]
    public async Task A_game_with_a_guide_can_still_be_deleted()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();
        var game = await AddGame(client, "Quake", ZipBytes("q"));

        (await SetGuide(client, game, baked: true)).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/GTGames/{game}")).EnsureSuccessStatusCode();

        Assert.Empty(Directory.GetFiles(Path.Combine(app.DataDirectory, "games")));
    }
}
