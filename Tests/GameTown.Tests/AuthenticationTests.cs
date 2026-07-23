using System.Net;
using System.Net.Http.Json;

namespace GameTown.Tests;

public class AuthenticationTests
{
    [Fact]
    public async Task Login_returns_the_user_and_sets_an_auth_cookie()
    {
        using var app = new GameTownApp();
        await app.CreateAdminAsync("owner", "ownerpassword");
        using var client = app.CreateBrowser();

        var response = await client.PostAsJsonAsync("/auth/login",
            new { username = "owner", password = "ownerpassword" });
        var user = await response.Content.ReadFromJsonAsync<CurrentUserDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("owner", user!.Username);
        Assert.Contains("Admin", user.Roles);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("gametown_auth"));
    }

    /// <summary>
    /// SecurePolicy must be SameAsRequest, not the ASP.NET default of Always. The appliance serves
    /// plain HTTP on a LAN, and a cookie marked Secure is silently discarded by the browser over
    /// HTTP — login appears to succeed and every subsequent request arrives anonymous.
    /// </summary>
    [Fact]
    public async Task The_auth_cookie_is_usable_over_plain_http()
    {
        using var app = new GameTownApp();
        await app.CreateAdminAsync("owner", "ownerpassword");
        using var client = app.CreateBrowser();

        var login = await client.PostAsJsonAsync("/auth/login",
            new { username = "owner", password = "ownerpassword" });
        var setCookie = login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("gametown_auth"));

        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// SameSite=Lax is the entire CSRF mitigation now that a browser attaches the credential rather
    /// than our code. See SECURITY-NOTES.md.
    /// </summary>
    [Fact]
    public async Task The_auth_cookie_is_samesite_lax()
    {
        using var app = new GameTownApp();
        await app.CreateAdminAsync("owner", "ownerpassword");
        using var client = app.CreateBrowser();

        var login = await client.PostAsJsonAsync("/auth/login",
            new { username = "owner", password = "ownerpassword" });
        var setCookie = login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("gametown_auth"));

        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bad_credentials_are_rejected()
    {
        using var app = new GameTownApp();
        await app.CreateAdminAsync("owner", "ownerpassword");
        using var client = app.CreateBrowser();

        var response = await client.PostAsJsonAsync("/auth/login",
            new { username = "owner", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_reports_anonymous_before_login_and_the_user_after()
    {
        using var app = new GameTownApp();
        await app.CreateAdminAsync("owner", "ownerpassword");
        using var client = app.CreateBrowser();

        var before = await client.GetAsync("/auth/me");
        await client.PostAsJsonAsync("/auth/login", new { username = "owner", password = "ownerpassword" });
        var after = await client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Logout_clears_the_session()
    {
        using var app = new GameTownApp();
        using var client = await app.SignInAsAdminAsync();

        await client.PostAsync("/auth/logout", null);
        var response = await client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record CurrentUserDto(string Username, List<string> Roles);
}
