using API.Services;
using GameTown.Contracts.Games;
using System.Net;
using System.Net.Http.Json;

namespace GameTown.Tests;

/// <summary>
/// Manual tags, and the filter built on them.
///
/// The cases here are the ones that fail quietly. A tag list that slowly fills with "Co-op", "co op"
/// and "COOP" still works — it just stops being useful, one duplicate at a time — and an AND filter
/// that is secretly an OR returns results, just the wrong ones.
/// </summary>
public class TagTests
{
    /// <summary>
    /// Adds a game and returns its id. Through the real upload endpoint rather than an INSERT,
    /// because the tag endpoints are reached with a game id and this is where one comes from.
    ///
    /// The archive body is derived from the title so that two calls send different bytes. Identical
    /// content is refused with a 409 by the SHA-256 dedupe guard — correctly, and it caught this
    /// helper the first time a test here added two games.
    /// </summary>
    private static async Task<Guid> AddGame(HttpClient client, string title)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent("Unzip and run"), "howTo" },
            { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes($"archive for {title}")), "file", "game.zip" },
        };

        var response = await client.PostAsync("/GTGames/Add", content);
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<AddGameResponse>();
        return created!.Id;
    }

    private static async Task<List<TagContract>> SetTags(HttpClient client, Guid id, params string[] names)
    {
        var response = await client.PutAsJsonAsync($"/tags/game/{id}", new SetGameTagsRequest { Names = [.. names] });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<TagContract>>())!;
    }

    // ------------------------------------------------------------------ the vocabulary

    /// <summary>
    /// The four quick-add tags ship with the schema, so the editor has buttons on a fresh install
    /// rather than an empty box and no hint about what tags are for.
    /// </summary>
    [Fact]
    public async Task A_fresh_install_has_the_quick_add_tags()
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var tags = await client.GetFromJsonAsync<List<TagContract>>("/tags/?quick=true");

        Assert.Equal(
            ["split-screen", "lan", "co-op", "competitive"],
            tags!.Select(t => t.Slug));
    }

    // ------------------------------------------------------------------ identity by slug

    /// <summary>
    /// The single most important behaviour here. Tags are typed by hand, by several people, over
    /// months — if the text is the identity then the list fills with near-duplicates and the filter
    /// bar becomes useless. Matching on a normalised slug is what prevents it.
    /// </summary>
    [Theory]
    [InlineData("LAN")]
    [InlineData("lan")]
    [InlineData("  Lan  ")]
    public async Task Differently_spelled_names_resolve_to_one_tag(string spelling)
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var game = await AddGame(client, "Quake");
        var applied = await SetTags(client, game, spelling);

        // It matched the seeded row rather than coining a second one, so it kept that row's display
        // name — a save from one game must not rename a tag under every other game carrying it.
        Assert.Single(applied);
        Assert.Equal("lan", applied[0].Slug);
        Assert.Equal("LAN", applied[0].Name);
        Assert.Equal("1", app.QueryScalar(@"SELECT COUNT(*) FROM ""Tags"" WHERE ""Slug"" = 'lan'"));
    }

    [Fact]
    public async Task An_unknown_name_is_created_as_a_new_tag()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var game = await AddGame(client, "Quake");
        var applied = await SetTags(client, game, "Hot Seat!");

        Assert.Equal("hot-seat", applied[0].Slug);
        Assert.Equal("Hot Seat!", applied[0].Name);
        Assert.False(applied[0].IsQuickAdd);
    }

    /// <summary>
    /// Guards the scaffolding trap documented in CLAUDE.md: the scaffolder marks a Guid key with no
    /// database default as ValueGeneratedNever, which inserts Guid.Empty. The FIRST tag then saves
    /// fine and the second collides on the primary key — so a test that coins one tag would pass
    /// against the broken configuration.
    /// </summary>
    [Fact]
    public async Task Several_new_tags_get_distinct_ids()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var game = await AddGame(client, "Quake");
        var applied = await SetTags(client, game, "one", "two", "three");

        Assert.Equal(3, applied.Select(t => t.Id).Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, applied.Select(t => t.Id));
    }

    // ------------------------------------------------------------------ replacing a set

    [Fact]
    public async Task Replacing_the_set_drops_the_tags_left_out()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var game = await AddGame(client, "Quake");
        await SetTags(client, game, "LAN", "Co-op");

        var applied = await SetTags(client, game, "LAN");

        Assert.Equal(["lan"], applied.Select(t => t.Slug));
        Assert.Equal("1", app.QueryScalar(
            $@"SELECT COUNT(*) FROM ""GameTownGame_Tags"" WHERE ""GameId"" = '{game.ToString().ToUpperInvariant()}'"));
    }

    /// <summary>
    /// Sending the same set twice must change nothing. The editor saves the list it is holding rather
    /// than a diff, so a retried request after a dropped response is an ordinary occurrence.
    /// </summary>
    [Fact]
    public async Task Applying_the_same_tags_twice_is_idempotent()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var game = await AddGame(client, "Quake");
        await SetTags(client, game, "LAN", "LAN", "lan");
        var second = await SetTags(client, game, "LAN");

        Assert.Single(second);
        Assert.Equal("1", app.QueryScalar(@"SELECT COUNT(*) FROM ""GameTownGame_Tags"""));
    }

    /// <summary>
    /// A tag nobody uses any more should stop appearing in the filter bar — otherwise a typo applied
    /// once and corrected is there forever. The quick-add four are exempt: they are the offered
    /// vocabulary, and a library with no co-op games in it still needs the button.
    /// </summary>
    [Fact]
    public async Task An_unused_tag_is_removed_but_a_quick_add_one_survives()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var game = await AddGame(client, "Quake");
        await SetTags(client, game, "Typpo", "LAN");
        await SetTags(client, game);

        Assert.Equal("0", app.QueryScalar(@"SELECT COUNT(*) FROM ""Tags"" WHERE ""Slug"" = 'typpo'"));
        Assert.Equal("1", app.QueryScalar(@"SELECT COUNT(*) FROM ""Tags"" WHERE ""Slug"" = 'lan'"));
    }

    /// <summary>
    /// A tag still carried by another game must not be swept up when one game drops it.
    /// </summary>
    [Fact]
    public async Task A_tag_still_in_use_elsewhere_is_kept()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var one = await AddGame(client, "Quake");
        var two = await AddGame(client, "Doom");
        await SetTags(client, one, "Deathmatch");
        await SetTags(client, two, "Deathmatch");

        await SetTags(client, one);

        Assert.Equal("1", app.QueryScalar(@"SELECT COUNT(*) FROM ""Tags"" WHERE ""Slug"" = 'deathmatch'"));
    }

    /// <summary>
    /// Deleting a game must take its tag links with it, which relies on ON DELETE CASCADE actually
    /// firing — and foreign keys are OFF in SQLite unless the connection string enables them. So this
    /// is really a test that "Foreign Keys=True" is still there: without it the links become orphan
    /// rows that keep a dead game's tags alive in every count.
    /// </summary>
    [Fact]
    public async Task Deleting_a_game_removes_its_tag_links()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var game = await AddGame(client, "Quake");
        await SetTags(client, game, "LAN");

        var deleted = await client.DeleteAsync($"/GTGames/{game}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal("0", app.QueryScalar(@"SELECT COUNT(*) FROM ""GameTownGame_Tags"""));
    }

    // ------------------------------------------------------------------ filtering

    [Fact]
    public async Task Filtering_by_two_tags_means_both_not_either()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var both = await AddGame(client, "Both Game");
        var lanOnly = await AddGame(client, "Lan Only");
        await SetTags(client, both, "LAN", "Co-op");
        await SetTags(client, lanOnly, "LAN");

        var results = await client.GetFromJsonAsync<List<GameContract>>(
            "/GTGames/getPaged/1/20?tags=lan,co-op");

        Assert.Equal(["Both Game"], results!.Select(g => g.Title));
    }

    /// <summary>
    /// A stale link — a tag that has since been renamed or swept — must return an empty shelf rather
    /// than a 500. The slug is in the URL, so links to filters outlive the tags in them.
    /// </summary>
    [Fact]
    public async Task An_unknown_tag_slug_returns_nothing_rather_than_failing()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        await AddGame(client, "Quake");

        var response = await client.GetAsync("/GTGames/getPaged/1/20?tags=does-not-exist");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<GameContract>>())!);
    }

    /// <summary>
    /// The free-text search matches tag names as well as titles, so typing "co-op" into the sidebar
    /// box does what the person typing it plainly meant.
    /// </summary>
    [Fact]
    public async Task The_search_box_matches_tag_names_as_well_as_titles()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var game = await AddGame(client, "Some Obscure Title");
        await SetTags(client, game, "Split screen");

        var results = await client.GetFromJsonAsync<List<GameContract>>(
            "/GTGames/search/?query=split&page=1&pageSize=20");

        Assert.Equal(["Some Obscure Title"], results!.Select(g => g.Title));
    }

    /// <summary>Tags come back on the game itself, or the shelf could not show them.</summary>
    [Fact]
    public async Task Tags_are_carried_on_the_game_contract()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var game = await AddGame(client, "Quake");
        await SetTags(client, game, "LAN");

        var fetched = await client.GetFromJsonAsync<GameContract>($"/GTGames/{game}");
        var listed = await client.GetFromJsonAsync<List<GameContract>>("/GTGames/getPaged/1/20");

        Assert.Equal(["LAN"], fetched!.Tags.Select(t => t.Name));
        Assert.Equal(["LAN"], listed!.Single().Tags.Select(t => t.Name));
    }

    // ------------------------------------------------------------------ authorization

    /// <summary>
    /// Reading is anonymous and writing is not — and the rejection must be a status code, not a 302
    /// to a login page, or fetch follows it and parses HTML as JSON.
    /// </summary>
    [Fact]
    public async Task Setting_tags_requires_a_contributor()
    {
        using var app = new GameTownApp();
        using var anonymous = app.CreateBrowser();

        var response = await anonymous.PutAsJsonAsync(
            $"/tags/game/{Guid.NewGuid()}", new SetGameTagsRequest { Names = ["LAN"] });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------ slug normalisation

    /// <summary>
    /// Pins the normalisation directly, because it is the rule the UNIQUE constraint depends on.
    /// Non-ASCII letters are kept rather than stripped: "Fælles skærm" must not collapse to a row of
    /// dashes, and SQLite's NOCASE folds ASCII only — which is exactly why lowercasing happens here
    /// in C# rather than being left to the database.
    /// </summary>
    [Theory]
    [InlineData("Split screen", "split-screen")]
    [InlineData("  LAN  ", "lan")]
    [InlineData("Co-op", "co-op")]
    [InlineData("Co   op", "co-op")]
    [InlineData("4 players!", "4-players")]
    [InlineData("Fælles skærm", "fælles-skærm")]
    [InlineData("!!!", "")]
    public void Slugify_normalises_a_name(string name, string expected)
        => Assert.Equal(expected, TagService.Slugify(name));

    /// <summary>A name that slugs to nothing is dropped rather than stored as a blank tag.</summary>
    [Fact]
    public void Names_that_normalise_to_nothing_are_discarded()
        => Assert.Empty(TagService.Normalise(["", "   ", "!!!", "---"]));
}
