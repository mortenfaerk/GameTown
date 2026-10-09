using GameTown.Contracts.Games;
using System.Net;
using System.Net.Http.Json;

namespace GameTown.Tests;

/// <summary>
/// The cursor-paged batch behind the endlessly scrolling library.
///
/// The test that justifies the whole endpoint is the one that adds games MID-WALK. Offset paging
/// passes every other case here; what it gets wrong is that a game inserted ahead of the reader shifts
/// every later page by one, and on an appended shelf that is a tile shown twice. Pagination hid it
/// because each page replaced the last. A cursor that is secretly an offset passes everything else.
/// </summary>
public class BrowseTests
{
    private static async Task<Guid> AddGame(HttpClient client, string title, string? body = null)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent("Unzip and run"), "howTo" },
            // Distinct bytes per call, or the SHA-256 dedupe guard refuses the second one.
            { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body ?? $"archive for {title}")), "file", "game.zip" },
        };

        var response = await client.PostAsync("/GTGames/Add", content);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AddGameResponse>())!.Id;
    }

    private static async Task<BrowsePageContract> Browse(HttpClient client, string query)
    {
        var response = await client.GetAsync("/GTGames/browse" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BrowsePageContract>())!;
    }

    /// <summary>Follows <c>next</c> to the end, the way the shelf does.</summary>
    private static async Task<List<GameContract>> WalkAll(HttpClient client, string filters, int limit)
    {
        var all = new List<GameContract>();
        string? next = null;
        var separator = filters.Length == 0 ? "?" : "&";
        for (var guard = 0; guard < 100; guard++)
        {
            var batch = await Browse(client, filters + separator + $"limit={limit}"
                                              + (next is null ? "" : $"&after={next}"));
            all.AddRange(batch.Items);
            next = batch.Next;
            if (next is null) return all;
        }
        throw new InvalidOperationException("The cursor never ended.");
    }

    [Fact]
    public async Task Walking_every_batch_returns_each_game_exactly_once_in_title_order()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        // Three sharing a title — once with different case, which NOCASE makes equal — so the batch
        // boundaries have to fall inside a run of equal titles, which is where the Id tiebreak matters.
        foreach (var title in new[] { "Quake", "Doom", "doom", "Doom", "Portal", "Abe", "Zork" })
            await AddGame(client, title, body: Guid.NewGuid().ToString());

        var walked = await WalkAll(client, "", limit: 2);

        Assert.Equal(7, walked.Count);
        Assert.Equal(7, walked.Select(g => g.Id).Distinct().Count());
        Assert.Equal(
            ["abe", "doom", "doom", "doom", "portal", "quake", "zork"],
            walked.Select(g => g.Title.ToLowerInvariant()));
    }

    [Fact]
    public async Task Games_added_mid_walk_neither_repeat_nor_vanish()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        foreach (var title in new[] { "B", "D", "F", "H" })
            await AddGame(client, title);

        var first = await Browse(client, "?limit=2");
        Assert.Equal(["B", "D"], first.Items.Select(g => g.Title));

        // One ahead of the reader — under offset paging this pushes "D" onto the next page — and one
        // behind, which the reader should still reach.
        await AddGame(client, "A");
        await AddGame(client, "G");

        var rest = new List<GameContract>();
        var next = first.Next;
        while (next is not null)
        {
            var batch = await Browse(client, $"?limit=2&after={next}");
            rest.AddRange(batch.Items);
            next = batch.Next;
        }

        Assert.Equal(["F", "G", "H"], rest.Select(g => g.Title));
    }

    [Fact]
    public async Task Total_counts_every_match_and_is_only_sent_on_the_first_batch()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        foreach (var title in new[] { "Alpha", "Bravo", "Charlie" })
            await AddGame(client, title);

        var first = await Browse(client, "?limit=2");
        Assert.Equal(3, first.Total);
        Assert.NotNull(first.Next);

        var second = await Browse(client, $"?limit=2&after={first.Next}");
        Assert.Null(second.Total);
        Assert.Null(second.Next);
        Assert.Equal(["Charlie"], second.Items.Select(g => g.Title));
    }

    [Fact]
    public async Task Filters_apply_to_the_batches_and_to_the_total()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var both = await AddGame(client, "Both Game");
        var lanOnly = await AddGame(client, "Lan Only");
        await AddGame(client, "Untagged");
        (await client.PutAsJsonAsync($"/tags/game/{both}", new SetGameTagsRequest { Names = ["LAN", "Co-op"] }))
            .EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/tags/game/{lanOnly}", new SetGameTagsRequest { Names = ["LAN"] }))
            .EnsureSuccessStatusCode();

        var tagged = await Browse(client, "?tags=lan,co-op");
        Assert.Equal(["Both Game"], tagged.Items.Select(g => g.Title));
        Assert.Equal(1, tagged.Total);

        var searched = await Browse(client, "?q=only");
        Assert.Equal(["Lan Only"], searched.Items.Select(g => g.Title));
        Assert.Equal(1, searched.Total);
    }

    [Fact]
    public async Task An_empty_library_is_one_empty_last_batch()
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var batch = await Browse(client, "");

        Assert.Empty(batch.Items);
        Assert.Null(batch.Next);
        Assert.Equal(0, batch.Total);
    }

    /// <summary>
    /// A cursor the server cannot read must be a 400, not "start from the top": the shelf would append
    /// the first batch again, then again, forever.
    /// </summary>
    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("eyJ9")]
    [InlineData("%%%")]
    public async Task A_malformed_cursor_is_rejected(string after)
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync($"/GTGames/browse?after={after}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10_000, 100)]
    public async Task The_batch_size_is_clamped(int asked, int expected)
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        for (var i = 0; i < 3; i++)
            await AddGame(client, $"Game {i}");

        var batch = await Browse(client, $"?limit={asked}");

        Assert.Equal(Math.Min(expected, 3), batch.Items.Count);
    }
}
