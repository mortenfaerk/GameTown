using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GameTown.Tests;

/// <summary>
/// A scripted LAN bot.
///
/// Holds the catalogue and the bindings the way the real one does, which is the only way these tests
/// mean anything: the rules under test are all about GameTown agreeing with what the bot reports, and
/// a handler that just returned canned JSON could not contradict GameTown the way a real service does.
///
/// Three behaviours, all VERIFIED against the live dev bot rather than inferred from its OpenAPI
/// document — none of them is obvious from it, and two are the opposite of what its shape suggests:
///
///  1. A PUT replaces the catalogue entry's TITLE. The entry holds exactly one.
///  2. It then binds every suggestion whose name equals that title AND IS CURRENTLY UNBOUND. An
///     already-bound suggestion is left alone and the response reports suggestionsBound = 0.
///  3. It never unbinds and never re-points anything, not even when the new title matches nothing.
///     Only DELETE removes bindings.
///
/// Getting this wrong in the fake would be worse than having no fake at all: every test here would
/// pass against a bot that behaves differently from the real one.
/// </summary>
public sealed class FakeLanBot : HttpMessageHandler
{
    private readonly List<(long Id, string Name, string Event, bool Played)> _suggestions = [];
    private readonly Dictionary<Guid, string> _catalogue = [];

    /// <summary>suggestion id -> matchId. The bot's own view of what is bound.</summary>
    private readonly Dictionary<long, Guid> _bindings = [];

    /// <summary>Set to make every call fail, for the tests about what GameTown does NOT record.</summary>
    public HttpStatusCode? FailWith { get; set; }

    public int PutCount { get; private set; }
    public int DeleteCount { get; private set; }

    public FakeLanBot Suggest(long id, string name, string lanEvent = "HCP #37 (2026)", bool played = false)
    {
        _suggestions.Add((id, name, lanEvent, played));
        return this;
    }

    /// <summary>A catalogue entry the crew made by hand inside the bot — not ours to delete.</summary>
    public FakeLanBot CrewBound(long suggestionId, Guid matchId, string title)
    {
        _catalogue[matchId] = title;
        _bindings[suggestionId] = matchId;
        return this;
    }

    /// <summary>The crew removing a catalogue entry inside the bot, with nothing telling GameTown.</summary>
    public void CrewReleased(Guid matchId)
    {
        _catalogue.Remove(matchId);
        foreach (var bound in _bindings.Where(b => b.Value == matchId).Select(b => b.Key).ToList())
            _bindings.Remove(bound);
    }

    public Guid? BindingFor(long suggestionId)
        => _bindings.TryGetValue(suggestionId, out var matchId) ? matchId : null;

    public bool HasCatalogueEntry(Guid matchId) => _catalogue.ContainsKey(matchId);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (FailWith is { } status)
            return Task.FromResult(new HttpResponseMessage(status));

        var path = request.RequestUri!.AbsolutePath;

        if (request.Method == HttpMethod.Get && path.EndsWith("/suggestions"))
            return Task.FromResult(Json(new
            {
                total = _suggestions.Count,
                items = _suggestions.Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    lanEventName = s.Event,
                    gameTownMatchId = BindingFor(s.Id),
                    played = s.Played,
                }),
            }));

        var matchId = Guid.Parse(path[(path.LastIndexOf('/') + 1)..]);

        if (request.Method == HttpMethod.Put)
        {
            PutCount++;
            var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync(cancellationToken).Result);
            var title = body.RootElement.GetProperty("title").GetString()!;

            // The title is replaced; existing bindings are NOT touched. Verified against the live bot:
            // a PUT carrying a title that matches nothing still leaves every previous binding in place.
            _catalogue[matchId] = title;

            // UNBOUND only. An already-bound suggestion is not re-pointed, even by a PUT carrying its
            // exact name — the live bot answers suggestionsBound = 0 for that, which is what makes
            // "GameTown cannot take over a link the crew already made" true rather than a guess.
            var bindings = _suggestions
                .Where(s => string.Equals(s.Name, title, StringComparison.OrdinalIgnoreCase))
                .Where(s => !_bindings.ContainsKey(s.Id))
                .ToList();

            foreach (var suggestion in bindings) _bindings[suggestion.Id] = matchId;

            return Task.FromResult(Json(new
            {
                created = true,
                suggestionsBound = bindings.Count,
                game = new { matchId, title, source = "gametown" },
            }));
        }

        if (request.Method == HttpMethod.Delete)
        {
            DeleteCount++;
            if (!_catalogue.Remove(matchId))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            foreach (var bound in _bindings.Where(b => b.Value == matchId).Select(b => b.Key).ToList())
                _bindings.Remove(bound);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
    }

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };
}

public class LanIntegrationTests
{
    private sealed record Suggestion(
        long RemoteId, string Name, string LanEventName, bool Played, Guid? GameId,
        string? GameTitle, string? LinkSource, bool IsBound, bool BoundElsewhere,
        bool Dismissed, string? DeepLink);

    private sealed record LinkResult(bool Ok, string Reason);

    private sealed record Candidate(Guid GameId, string Title, double Score, bool IsExact,
        List<string> AlreadyLinkedFrom);

    private sealed record Game(Guid Id, string Title, List<string> SuggestedFor, bool PlayedAtLan);

    private sealed record LanEvent(string Name, int GameCount);

    private sealed record Status(bool Configured, string? LastReason, int LastSeen, int LastMatched, int LastBound);

    /// <summary>
    /// Configures the integration through the real settings endpoint, so these tests also prove the
    /// values are readable by the running service without a restart — the same property SettingsTests
    /// pins for IGDB, and the one most likely to regress here because the sync runs on a timer.
    /// </summary>
    private static async Task ConfigureAsync(HttpClient admin)
    {
        var response = await admin.PatchAsJsonAsync("/settings", new
        {
            lanBotBaseUrl = "https://bot.example.test",
            lanBotApiKey = "test-key",
            publicBaseUrl = "http://gametown.test:5187",
        });

        response.EnsureSuccessStatusCode();
    }

    private static async Task<Guid> AddGameAsync(HttpClient client, string title)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent("Run it."), "howTo" },
            // Every archive must differ. The upload endpoint refuses a duplicate SHA-256 with 409 —
            // see UploadDeduplicationTests — so identical bytes would make the second AddGameAsync in
            // any test fail for a reason that has nothing to do with what it is testing.
            { new ByteArrayContent([0x50, 0x4B, 0x05, 0x06, .. new byte[18], .. Encoding.UTF8.GetBytes(title)]),
              "file", $"{Guid.NewGuid():N}.zip" },
        };

        var response = await client.PostAsync("/GTGames/Add", content);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Signs in as the admin the contributor helper already created. The harness's SignInAsAdminAsync
    /// always drives /setup, which 404s once an administrator exists.
    /// </summary>
    private static async Task<HttpClient> SignInExistingAdminAsync(GameTownApp app)
    {
        var client = app.CreateBrowser();
        var response = await client.PostAsJsonAsync("/auth/login",
            new { username = "admin", password = "adminpassword" });

        response.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<List<Suggestion>> SuggestionsAsync(HttpClient client, string state = "all")
        => await client.GetFromJsonAsync<List<Suggestion>>($"/lan/suggestions?state={state}") ?? [];

    [Fact]
    public async Task Lan_routes_require_an_account_and_answer_401_not_a_redirect()
    {
        using var app = new GameTownApp();
        using var anonymous = app.CreateBrowser();

        foreach (var route in new[] { "/lan/suggestions", "/lan/count", "/lan/status" })
        {
            var response = await anonymous.GetAsync(route);

            // 401, never a 302 to a login page. A redirect is followed by fetch and the SPA then
            // parses HTML as JSON — the failure AuthenticationTests exists to pin, repeated here
            // because a new endpoint group is exactly where it comes back.
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task An_unconfigured_install_reports_not_configured_and_calls_nothing()
    {
        var bot = new FakeLanBot().Suggest(1, "Portal 2");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var status = await admin.GetFromJsonAsync<Status>("/lan/status");
        Assert.False(status!.Configured);

        var synced = await admin.PostAsync("/lan/sync", null);
        synced.EnsureSuccessStatusCode();

        var after = await synced.Content.ReadFromJsonAsync<Status>();
        Assert.Equal("not-configured", after!.LastReason);

        // Nothing was pulled and nothing was written, which is the point: an install that has never
        // heard of the LAN bot must behave exactly as it did before this feature existed.
        Assert.Empty(await SuggestionsAsync(admin));
        Assert.Equal(0, bot.PutCount);
    }

    [Fact]
    public async Task Sync_matches_on_case_and_punctuation_and_pushes_the_binding()
    {
        var bot = new FakeLanBot().Suggest(3, "Counter-strike 2").Suggest(9, "test");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var gameId = await AddGameAsync(admin, "Counter-Strike 2");
        await ConfigureAsync(admin);

        var status = await (await admin.PostAsync("/lan/sync", null)).Content.ReadFromJsonAsync<Status>();

        Assert.Equal("ok", status!.LastReason);
        Assert.Equal(2, status.LastSeen);
        Assert.Equal(1, status.LastMatched);
        Assert.Equal(1, status.LastBound);

        // The bot's own view: the matchId is the GameTown game's id, which is what makes the deep
        // link the bot composes resolve.
        Assert.Equal(gameId, bot.BindingFor(3));

        var matched = Assert.Single(await SuggestionsAsync(admin, "matched"));
        Assert.Equal("auto", matched.LinkSource);
        Assert.True(matched.IsBound);
        Assert.Equal($"http://gametown.test:5187/game/{gameId}", matched.DeepLink);

        // "test" is not a game and must stay in the wishlist rather than being attached to anything.
        var unmatched = Assert.Single(await SuggestionsAsync(admin, "unmatched"));
        Assert.Equal("test", unmatched.Name);
    }

    /// <summary>
    /// THE THRASH TEST, and the reason it syncs three times.
    ///
    /// Two suggestions normalise to the same game. Each gets its own binding — bindings are additive —
    /// but the catalogue entry holds ONE TITLE, so a sync that re-pushed would leave that title
    /// flipping between "Quake 3 Arena" and "Quake III Arena" on every tick, forever, logging nothing.
    /// The Discord entry's label would simply never settle.
    ///
    /// A single sync cannot show that: it passes trivially either way. The assertion that matters is
    /// the PUT count after convergence.
    /// </summary>
    [Fact]
    public async Task Two_suggestions_for_one_game_both_get_linked_and_then_nothing_is_re_pushed()
    {
        var bot = new FakeLanBot().Suggest(1, "Quake 3 Arena").Suggest(2, "Quake III Arena");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var gameId = await AddGameAsync(admin, "Quake III Arena");
        await ConfigureAsync(admin);

        await admin.PostAsync("/lan/sync", null);

        // Both matched AND both bound: the bot binds each suggestion by its own name and never
        // unbinds, so a game asked for under two spellings really does carry two working links.
        var matched = await SuggestionsAsync(admin, "matched");
        Assert.Equal(2, matched.Count);
        Assert.All(matched, s => Assert.Equal(gameId, s.GameId));
        Assert.All(matched, s => Assert.True(s.IsBound));

        Assert.Equal(gameId, bot.BindingFor(1));
        Assert.Equal(gameId, bot.BindingFor(2));

        var putsAfterFirst = bot.PutCount;

        await admin.PostAsync("/lan/sync", null);
        await admin.PostAsync("/lan/sync", null);

        // Converged. No further writes at all against the crew's system, so the catalogue title stays
        // put instead of oscillating between the two names.
        Assert.Equal(putsAfterFirst, bot.PutCount);
        Assert.Equal(gameId, bot.BindingFor(1));
        Assert.Equal(gameId, bot.BindingFor(2));
    }

    /// <summary>
    /// A suggestion the crew already bound inside the bot cannot be claimed by GameTown, and the
    /// screen must not pretend otherwise.
    ///
    /// The bot binds only suggestions that are currently UNBOUND, so the PUT lands (the catalogue
    /// entry is written) while the suggestion keeps pointing at the crew's entry. Reporting that as a
    /// successful link would show a green tick in GameTown for a link Discord does not have — which is
    /// exactly the kind of quiet lie this codebase spends its comments guarding against.
    ///
    /// GameTown still records the GAME, because it genuinely knows what it is: the badge, the shelf
    /// and the wishlist all read that rather than the binding.
    /// </summary>
    [Fact]
    public async Task Linking_a_suggestion_the_crew_already_bound_records_the_game_but_says_the_link_did_not_move()
    {
        var bot = new FakeLanBot();
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var gameId = await AddGameAsync(admin, "Quake III Arena");

        // Bound inside the bot to the crew's OWN catalogue entry, which is not a GameTown game.
        var crewEntry = Guid.NewGuid();
        bot.Suggest(5, "Quake arena").CrewBound(5, crewEntry, "Quake arena");

        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);

        var result = await (await admin.PostAsJsonAsync("/lan/link", new { remoteId = 5L, gameId }))
            .Content.ReadFromJsonAsync<LinkResult>();

        // A success, not a failure — but a different one, and the caller has to be able to tell.
        Assert.True(result!.Ok);
        Assert.Equal("bound-elsewhere", result.Reason);

        // The bot did not move it.
        Assert.Equal(crewEntry, bot.BindingFor(5));

        var matched = Assert.Single(await SuggestionsAsync(admin, "matched"));
        Assert.Equal(gameId, matched.GameId);
        Assert.False(matched.IsBound);
        Assert.True(matched.BoundElsewhere);

        // And the game still carries the suggestion everywhere a visitor sees it.
        using var anonymous = app.CreateBrowser();
        var games = await anonymous.GetFromJsonAsync<List<Game>>("/GTGames/getPaged/1/50");
        Assert.Equal(["HCP #37 (2026)"], games!.Single().SuggestedFor);
    }

    /// <summary>
    /// Linking a game another suggestion already points at is allowed and takes nothing away.
    ///
    /// This is the behaviour the bot actually has, verified live: a PUT adds a binding for the
    /// suggestions matching its title and leaves every existing one alone.
    /// </summary>
    [Fact]
    public async Task Linking_a_game_that_is_already_linked_adds_a_binding_rather_than_moving_one()
    {
        var bot = new FakeLanBot().Suggest(1, "Quake 3 Arena").Suggest(4, "Wardogs");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var gameId = await AddGameAsync(admin, "Quake 3 Arena");
        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);

        var response = await admin.PostAsJsonAsync("/lan/link", new { remoteId = 4L, gameId });
        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadFromJsonAsync<LinkResult>())!.Ok);

        // Both, not one. The auto-matched link is untouched.
        Assert.Equal(gameId, bot.BindingFor(1));
        Assert.Equal(gameId, bot.BindingFor(4));

        var matched = await SuggestionsAsync(admin, "matched");
        Assert.Equal(2, matched.Count);
        Assert.All(matched, s => Assert.True(s.IsBound));
    }

    /// <summary>
    /// A link that could not be pushed must not be recorded.
    ///
    /// Otherwise GameTown shows a green tick for a Discord link that does not exist — and the next
    /// sync, reconciling against a bot that has never heard of it, would silently undo it anyway.
    /// </summary>
    [Fact]
    public async Task A_link_whose_push_failed_is_not_recorded()
    {
        var bot = new FakeLanBot().Suggest(4, "Wardogs");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var gameId = await AddGameAsync(admin, "Wardogs");
        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);
        await admin.PostAsJsonAsync("/lan/unlink", new { remoteId = 4L });

        bot.FailWith = HttpStatusCode.ServiceUnavailable;

        var result = await (await admin.PostAsJsonAsync("/lan/link",
            new { remoteId = 4L, gameId })).Content.ReadFromJsonAsync<LinkResult>();

        Assert.False(result!.Ok);
        Assert.Equal("unreachable", result.Reason);

        bot.FailWith = null;
        Assert.Empty(await SuggestionsAsync(admin, "matched"));
    }

    /// <summary>
    /// A binding that vanishes at the far end is rebuilt on the next sync.
    ///
    /// Reconciliation releases it — the bot is the authority on what is bound — but GameTown keeps
    /// knowing which game the suggestion is, and the push phase then re-creates the catalogue entry.
    /// The net effect is that a bot restored from backup, or an outage that lost writes, heals itself
    /// rather than leaving every Discord link quietly dead.
    ///
    /// The trade-off, stated because it is a real one: a crew member deleting a GameTown-created entry
    /// by hand inside the bot will see it come back. Their way to make it stick is to unlink or dismiss
    /// it in GameTown, which clears the match rather than just the binding.
    /// </summary>
    [Fact]
    public async Task A_binding_the_bot_lost_is_rebuilt_on_the_next_sync()
    {
        var bot = new FakeLanBot().Suggest(4, "Wardogs");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var gameId = await AddGameAsync(admin, "Wardogs");
        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);
        Assert.True((await SuggestionsAsync(admin, "matched")).Single().IsBound);

        // The entry disappears at the far end. Nothing tells GameTown.
        bot.CrewReleased(gameId);
        Assert.False(bot.HasCatalogueEntry(gameId));

        await admin.PostAsync("/lan/sync", null);

        var suggestion = (await SuggestionsAsync(admin, "matched")).Single();
        Assert.Equal(gameId, suggestion.GameId);
        Assert.True(suggestion.IsBound);
        Assert.Equal(gameId, bot.BindingFor(4));

        // Rebuilt rather than assumed: the local row was demoted and pushed again, not left claiming
        // a binding nobody checked.
        Assert.True(bot.HasCatalogueEntry(gameId));
    }

    /// <summary>
    /// Unlinking in GameTown, unlike a delete at the far end, is permanent — it clears the MATCH and
    /// not just the binding, so the push phase has nothing to re-assert.
    /// </summary>
    [Fact]
    public async Task An_unlinked_suggestion_is_not_pushed_again_by_the_next_sync()
    {
        var bot = new FakeLanBot().Suggest(4, "Wardogs");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var gameId = await AddGameAsync(admin, "Wardogs");
        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);

        await admin.PostAsJsonAsync("/lan/unlink", new { remoteId = 4L });
        await admin.PostAsync("/lan/sync", null);
        await admin.PostAsync("/lan/sync", null);

        Assert.Null(bot.BindingFor(4));
        Assert.False(bot.HasCatalogueEntry(gameId));
        Assert.Single(await SuggestionsAsync(admin, "unmatched"));
    }

    /// <summary>
    /// An unlink must never delete a catalogue entry the crew made by hand inside the bot. PushedAtUtc
    /// is what tells the two apart.
    /// </summary>
    [Fact]
    public async Task Unlinking_an_adopted_binding_leaves_the_crews_entry_alone()
    {
        // The bot is created BEFORE the app, because the handler is installed while the host is
        // built — assigning LanBotHandler after the first request has no effect. It is filled in
        // afterwards instead, which works because the fake is mutable and captured by reference.
        var bot = new FakeLanBot();
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var gameId = await AddGameAsync(admin, "Overcooked 2");
        bot.Suggest(6, "overcooked 2").CrewBound(6, gameId, "overcooked 2");

        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);

        var adopted = (await SuggestionsAsync(admin, "matched")).Single();
        Assert.Equal("remote", adopted.LinkSource);
        Assert.True(adopted.IsBound);

        var result = await (await admin.PostAsJsonAsync("/lan/unlink", new { remoteId = 6L }))
            .Content.ReadFromJsonAsync<LinkResult>();

        Assert.True(result!.Ok);
        Assert.Equal(0, bot.DeleteCount);
        Assert.True(bot.HasCatalogueEntry(gameId));
    }

    [Fact]
    public async Task Unlinking_what_we_pushed_deletes_the_catalogue_entry()
    {
        var bot = new FakeLanBot().Suggest(4, "Wardogs");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var gameId = await AddGameAsync(admin, "Wardogs");
        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);

        var result = await (await admin.PostAsJsonAsync("/lan/unlink", new { remoteId = 4L }))
            .Content.ReadFromJsonAsync<LinkResult>();

        Assert.True(result!.Ok);
        Assert.Equal(1, bot.DeleteCount);
        Assert.False(bot.HasCatalogueEntry(gameId));
        Assert.Null(bot.BindingFor(4));
    }

    [Fact]
    public async Task Deleting_a_game_returns_its_suggestion_to_the_wishlist()
    {
        var bot = new FakeLanBot().Suggest(4, "Wardogs");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var gameId = await AddGameAsync(admin, "Wardogs");
        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);

        (await admin.DeleteAsync($"/GTGames/{gameId}")).EnsureSuccessStatusCode();

        // ON DELETE SET NULL, which only fires because the connection forces Foreign Keys=True. The
        // suggestion is work again, which is the right prompt for someone to re-upload it.
        var unmatched = Assert.Single(await SuggestionsAsync(admin, "unmatched"));
        Assert.Equal(4, unmatched.RemoteId);
        Assert.Null(unmatched.GameId);
    }

    [Fact]
    public async Task A_dismissed_suggestion_leaves_the_wishlist_and_is_never_auto_matched()
    {
        var bot = new FakeLanBot().Suggest(9, "test");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);

        (await admin.PostAsJsonAsync("/lan/dismiss", new { remoteId = 9L, dismissed = true }))
            .EnsureSuccessStatusCode();

        Assert.Empty(await SuggestionsAsync(admin, "unmatched"));
        Assert.Single(await SuggestionsAsync(admin, "dismissed"));

        var count = await admin.GetFromJsonAsync<JsonElement>("/lan/count");
        Assert.Equal(0, count.GetProperty("unmatched").GetInt32());

        // A later upload of something called "test" must not resurrect it: dismissal is a judgement,
        // and the sync has to keep honouring it.
        await AddGameAsync(admin, "test");
        await admin.PostAsync("/lan/sync", null);

        Assert.Empty(await SuggestionsAsync(admin, "matched"));
        Assert.Equal(0, bot.PutCount);
    }

    [Fact]
    public async Task Candidates_are_ranked_and_say_when_choosing_one_would_replace_a_binding()
    {
        var bot = new FakeLanBot().Suggest(1, "Quake 3 Arena").Suggest(4, "Quake Arena");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var quake3 = await AddGameAsync(admin, "Quake III Arena");
        await AddGameAsync(admin, "Portal 2");
        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);

        var candidates = await admin.GetFromJsonAsync<List<Candidate>>("/lan/suggestions/4/candidates");

        var best = candidates![0];
        Assert.Equal(quake3, best.GameId);

        // Not an exact match — "Quake Arena" and "Quake III Arena" are different strings — so it is
        // offered, never applied. And it names what else already links to that game, as information:
        // choosing it is allowed and costs the existing link nothing.
        Assert.False(best.IsExact);
        Assert.Equal(["Quake 3 Arena"], best.AlreadyLinkedFrom);
    }

    [Fact]
    public async Task Games_carry_the_events_they_were_suggested_for_and_the_shelf_can_filter_on_them()
    {
        var bot = new FakeLanBot()
            .Suggest(1, "Portal 2", "HCP #37 (2026)", played: true)
            .Suggest(2, "Quake 3 Arena", "HCP #36 (2025)");

        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        await AddGameAsync(admin, "Portal 2");
        await AddGameAsync(admin, "Quake 3 Arena");
        await AddGameAsync(admin, "Wardogs");
        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);

        // Anonymous: the shelf and its filters are public, like the rest of browsing.
        using var anonymous = app.CreateBrowser();

        var all = await anonymous.GetFromJsonAsync<List<Game>>("/GTGames/getPaged/1/50");
        Assert.Equal(3, all!.Count);

        var portal = all.Single(g => g.Title == "Portal 2");
        Assert.Equal(["HCP #37 (2026)"], portal.SuggestedFor);
        Assert.True(portal.PlayedAtLan);
        Assert.Empty(all.Single(g => g.Title == "Wardogs").SuggestedFor);

        var filtered = await anonymous.GetFromJsonAsync<List<Game>>(
            $"/GTGames/getPaged/1/50?lan={Uri.EscapeDataString("HCP #37 (2026)")}");

        Assert.Equal("Portal 2", Assert.Single(filtered!).Title);

        var events = await anonymous.GetFromJsonAsync<List<LanEvent>>("/lan/events");
        Assert.Equal(2, events!.Count);
        Assert.All(events, e => Assert.Equal(1, e.GameCount));

        // A stale link degrades to an empty shelf rather than a 400, matching how ?tags= behaves.
        var unknown = await anonymous.GetFromJsonAsync<List<Game>>("/GTGames/getPaged/1/50?lan=HCP%20%231");
        Assert.Empty(unknown!);
    }

    /// <summary>
    /// Suggestion names and event names are typed by players in Discord and reach GameTown through a
    /// third party, so they are untrusted text on pages GameTown renders.
    ///
    /// Two things are pinned, and it is worth being precise about which is the defence. The route
    /// answers <c>application/json</c>, so a browser pointed straight at it never renders the markup
    /// as a document — that is the same assertion ApiRoutingTests makes, repeated here because a new
    /// endpoint group is where SPA-fallback hosting quietly turns a route into <c>200 text/html</c>.
    /// And the text round-trips byte for byte, because nothing here may "clean" it: these strings are
    /// rendered by Razor text interpolation, which escapes them, and a sanitiser in the middle would
    /// only corrupt legitimate titles while adding no safety.
    ///
    /// The contrast worth remembering is game DESCRIPTIONS, which go through MarkupString and
    /// therefore do need <c>GameMappings</c>' sanitiser — see SanitizerTests. These do not, and must
    /// never be given to MarkupString.
    /// </summary>
    [Fact]
    public async Task Player_typed_names_are_carried_as_data_not_as_markup()
    {
        const string nastyName = "<img src=x onerror=alert(1)>";
        const string nastyEvent = "<script>alert(2)</script>";

        var bot = new FakeLanBot().Suggest(1, nastyName, nastyEvent);
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);

        var response = await admin.GetAsync("/lan/suggestions?state=all");
        response.EnsureSuccessStatusCode();

        // Not text/html. Asserted on the media type alone because the charset parameter is noise here.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var suggestion = (await SuggestionsAsync(admin)).Single();
        Assert.Equal(nastyName, suggestion.Name);
        Assert.Equal(nastyEvent, suggestion.LanEventName);
    }

    [Fact]
    public async Task A_failed_pull_does_not_unbind_anything()
    {
        var bot = new FakeLanBot().Suggest(4, "Wardogs");
        using var app = new GameTownApp { LanBotHandler = bot };
        using var admin = await app.SignInAsAdminAsync();

        var gameId = await AddGameAsync(admin, "Wardogs");
        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);

        bot.FailWith = HttpStatusCode.GatewayTimeout;
        var status = await (await admin.PostAsync("/lan/sync", null)).Content.ReadFromJsonAsync<Status>();
        bot.FailWith = null;

        Assert.Equal("unreachable", status!.LastReason);

        // The dangerous version of this bug: an unreachable bot returns an empty list, which looks
        // exactly like every suggestion having been withdrawn — and reconciliation would then release
        // the whole library's worth of bindings on the strength of a network blip.
        var suggestion = (await SuggestionsAsync(admin, "matched")).Single();
        Assert.True(suggestion.IsBound);
        Assert.Equal(gameId, suggestion.GameId);
    }

    [Fact]
    public async Task Contributors_may_link_but_only_admins_may_sync()
    {
        var bot = new FakeLanBot().Suggest(4, "Wardogs");
        using var app = new GameTownApp { LanBotHandler = bot };

        // Contributor first: SignInAsContributorAsync creates the admin itself, and /setup 404s once
        // one exists — so asking for an admin first would leave the wizard with nothing to do.
        using var contributor = await app.SignInAsContributorAsync();
        using var admin = await SignInExistingAdminAsync(app);

        var gameId = await AddGameAsync(admin, "Wardogs");
        await ConfigureAsync(admin);
        await admin.PostAsync("/lan/sync", null);
        await admin.PostAsJsonAsync("/lan/unlink", new { remoteId = 4L });

        // The wishlist is a contributor's to-do list, so linking is theirs to do.
        var linked = await contributor.PostAsJsonAsync("/lan/link", new { remoteId = 4L, gameId });
        linked.EnsureSuccessStatusCode();

        // Running a sync spends someone else's rate limit; the status panel reports on the
        // integration rather than the library. Both stay Admin.
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.PostAsync("/lan/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.GetAsync("/lan/status")).StatusCode);
    }
}
