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
        "/GTGames/browse",
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

    /// <summary>
    /// The API documentation is part of the intentionally-anonymous surface, so it belongs in this
    /// file's matrix — but only once an admin has turned it on.
    ///
    /// It cannot join <see cref="AnonymousRoutes"/>, which asserts against a default install: these
    /// routes are off by default and answer 404 until the ApiDocsEnabled setting is saved. That is
    /// the point of the setting, and SettingsTests owns it (see
    /// <c>SettingsTests.Api_documentation_is_off_by_default</c> and the three tests after it).
    ///
    /// What this test adds is the authorization claim specifically: while hosted, the docs are
    /// readable with no cookie, and turning them on opens nothing else. Anonymous rather than
    /// Admin-gated is deliberate — see accepted risk 12 in SECURITY-NOTES.md — and a later change
    /// that quietly put them behind the FallbackPolicy would hand a signed-out visitor a bare 401
    /// with no login page to land on.
    /// </summary>
    [Fact]
    public async Task Hosted_api_documentation_is_anonymous_and_widens_nothing_else()
    {
        using var app = new GameTownApp();
        using var admin = await app.SignInAsAdminAsync();
        await admin.PatchAsJsonAsync("/settings", new { apiDocsEnabled = true });

        using var client = app.CreateBrowser();

        var document = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Equal("application/json", document.Content.Headers.ContentType?.MediaType);

        var ui = await client.GetAsync("/scalar/v1");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);

        // Every route the document describes keeps the authorization it already had. The two Admin
        // routes above are re-asserted here on purpose: this is the pairing that makes publishing
        // the API's shape to a LAN acceptable, and it has to fail together with the claim above.
        foreach (var guarded in new[] { "/settings", "/users/getAll" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(guarded)).StatusCode);
        }
    }
}
