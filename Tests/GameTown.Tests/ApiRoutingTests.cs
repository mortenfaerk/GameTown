using System.Net;

namespace GameTown.Tests;

/// <summary>
/// Guards the single nastiest failure mode of hosting the SPA and the API on one origin: a route that
/// does not match no longer 404s, it falls through to MapFallbackToFile and returns
/// <c>200 text/html</c>. The caller sees success and parses the SPA shell as JSON.
///
/// That is exactly what shipped. Every GET declaring <c>.Accepts&lt;T&gt;(...)</c> carried a
/// content-type constraint, and a GET sends no Content-Type, so the library listing, search,
/// get-by-id, download and both /meta routes were unmatchable by any browser. It went unnoticed
/// because the status code was 200.
///
/// **Asserting on the status code alone would not catch this. Assert the content type.**
/// </summary>
public class ApiRoutingTests
{
    public static TheoryData<string> JsonRoutes() =>
    [
        "/GTGames/getPaged/1/5",
        "/GTGames/search/?query=x&page=1&pageSize=5",
        // The tag filter travels as a query parameter on both of the above. A typo in the parameter
        // name would not 400 — the routes would simply match without it and quietly return the
        // unfiltered library, which looks like a filter that does not work rather than a broken URL.
        "/GTGames/getPaged/1/5?tags=lan",
        "/GTGames/search/?query=x&page=1&pageSize=5&tags=lan,co-op",
        // Anonymous, because the library's filter bar is. Behind the fallback authorization policy
        // this would answer 401 rather than falling through to the SPA, but the assertion below is
        // the one that matters either way.
        "/tags/",
        "/tags/?quick=true",
        "/tags/game/11111111-1111-1111-1111-111111111111",
    ];

    [Theory]
    [MemberData(nameof(JsonRoutes))]
    public async Task Api_routes_return_json_not_the_spa_shell(string route)
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync(route);
        var mediaType = response.Content.Headers.ContentType?.MediaType;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", mediaType);
    }

    /// <summary>
    /// The same guard for admin routes, which have their own <c>.Accepts</c> history
    /// (<c>GET /users/get</c>, <c>DELETE /users/delete</c>).
    /// </summary>
    [Fact]
    public async Task Admin_json_routes_return_json()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        foreach (var route in new[] { "/users/getAll", "/users/getAllRoles", "/settings" })
        {
            var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    /// <summary>
    /// The box-art search, which is Contributor-gated and so cannot go in the anonymous theory above.
    ///
    /// Worth its own case because it answers 200 for every outcome including "no provider is
    /// configured" — which is what this test environment is. A route that had fallen through to the
    /// SPA would also answer 200, so the content type is the only thing separating the two.
    /// </summary>
    [Fact]
    public async Task The_box_art_search_returns_json_even_with_no_provider_configured()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await client.GetAsync("/boxart/search?title=Portal");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// The flip side: a path that really is a client-side route must still reach the SPA, or deep
    /// links and refreshes break.
    /// </summary>
    [Fact]
    public async Task Client_side_routes_fall_back_to_the_spa()
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync("/addgame");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
