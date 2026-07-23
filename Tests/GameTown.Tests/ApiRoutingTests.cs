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
