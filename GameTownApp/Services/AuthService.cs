using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using System.Net.Http.Json;
using System.Security.Claims;

namespace GameTownApp.Services;

/// <summary>
/// Authentication state, backed by the server's auth cookie.
///
/// The browser holds an HttpOnly cookie it cannot read, so this class does not — and cannot — hold a
/// token. It asks the server who the caller is (GET /auth/me) and caches the answer for rendering.
/// That is a genuine improvement on the JWT arrangement it replaced, where the SPA parsed claims out
/// of a token sitting in WASM memory: readable by any injected script, and self-asserted rather than
/// server-asserted.
///
/// Note what is NOT here any more: no token expiry tracking, no refresh timer, no delegating
/// handler. The cookie's sliding expiration renews it server-side, so there is nothing to schedule.
/// </summary>
public class AuthService : AuthenticationStateProvider
{
    private readonly HttpClient _http;

    public string? Username { get; private set; }
    public List<string> Roles { get; private set; } = [];
    public bool IsAuthenticated => Username is not null;

    public AuthService(string apiBaseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(apiBaseUrl) };
    }

    public async Task<bool> LoginAsync(LoginRequest model)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "auth/login")
        {
            Content = JsonContent.Create(model)
        };
        // Same-origin now, but stated explicitly: this request both sends and receives the auth
        // cookie, and it is the one call where getting that wrong silently produces a signed-in
        // server and a signed-out browser.
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Apply(null);
            return false;
        }

        Apply(await response.Content.ReadFromJsonAsync<CurrentUser>());
        return IsAuthenticated;
    }

    /// <summary>
    /// Asks the server who we are. Returns false when anonymous — which is an ordinary answer, not
    /// an error: a visitor with no cookie gets 401 here and simply browses the library signed out.
    /// </summary>
    public async Task<bool> RefreshUserAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "auth/me");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Apply(null);
            return false;
        }

        Apply(await response.Content.ReadFromJsonAsync<CurrentUser>());
        return IsAuthenticated;
    }

    public async Task LogoutAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "auth/logout");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        try
        {
            await _http.SendAsync(request);
        }
        catch
        {
            // The server being unreachable must not trap someone in a signed-in UI; drop the local
            // state regardless and let the next call discover the truth.
        }
        Apply(null);
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var identity = IsAuthenticated
            ? new ClaimsIdentity(BuildClaims(), authenticationType: "gametown")
            : new ClaimsIdentity();

        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }

    private IEnumerable<Claim> BuildClaims()
    {
        yield return new Claim(ClaimTypes.Name, Username!);
        foreach (var role in Roles)
        {
            yield return new Claim(ClaimTypes.Role, role);
        }
    }

    private void Apply(CurrentUser? user)
    {
        Username = user?.Username;
        Roles = user?.Roles ?? [];
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
