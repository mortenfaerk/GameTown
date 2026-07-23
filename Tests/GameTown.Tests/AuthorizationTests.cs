using System.Net;
using System.Net.Http.Json;

namespace GameTown.Tests;

/// <summary>
/// The anonymous / Contributor / Admin matrix.
///
/// This exists because three endpoints once shipped anonymous by accident (game update, game delete,
/// and all of /meta), which is what the authorization FallbackPolicy was introduced to prevent. A
/// fallback policy proves authentication but never a role, so the role requirements still have to be
/// asserted rather than assumed.
/// </summary>
public class AuthorizationTests
{
    public static TheoryData<string> AnonymousRoutes() =>
    [
        "/GTGames/getPaged/1/5",
        "/GTGames/search/?query=x&page=1&pageSize=5",
        "/auth/me",
    ];

    [Theory]
    [MemberData(nameof(AnonymousRoutes))]
    public async Task Public_routes_do_not_require_a_login(string route)
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync(route);

        // /auth/me answers 401 when anonymous, which is its correct answer rather than a rejection.
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized,
            $"{route} returned {(int)response.StatusCode}");
    }

    [Theory]
    [InlineData("/users/getAll")]
    [InlineData("/users/getAllRoles")]
    [InlineData("/settings")]
    public async Task Admin_routes_reject_anonymous_callers(string route)
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/users/getAll")]
    [InlineData("/settings")]
    public async Task Admin_routes_reject_a_contributor(string route)
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsContributorAsync();

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_routes_admit_an_admin()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        var response = await client.GetAsync("/users/getAll");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Guards the cookie handler's OnRedirectToLogin/OnRedirectToAccessDenied overrides. Left at
    /// their defaults these answer with a 302 to a login page, which fetch follows to a
    /// 200 text/html — so a caller sees success and parses a web page as JSON.
    /// </summary>
    [Fact]
    public async Task Rejections_are_status_codes_and_never_redirects()
    {
        using var app = new GameTownApp();
        using var anonymous = app.CreateBrowser();
        using var contributor = await app.SignInAsContributorAsync();

        var unauthenticated = await anonymous.GetAsync("/users/getAll");
        var forbidden = await contributor.GetAsync("/settings");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Null(unauthenticated.Headers.Location);
        Assert.Null(forbidden.Headers.Location);
    }

    /// <summary>
    /// The SPA shell must stay anonymous. It is an endpoint like any other, so the FallbackPolicy
    /// would otherwise put the page that lets you sign in behind being signed in.
    /// </summary>
    [Fact]
    public async Task The_spa_shell_is_reachable_when_signed_out()
    {
        using var app = new GameTownApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
