using GameTownApp.Models.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using System.Net.Http.Json;
using System.Security.Claims;

namespace GameTownApp.Services;

public class AuthService : AuthenticationStateProvider
{
    private readonly HttpClient _http;
    private string? _jwtToken;
    private DateTime? _tokenExpiration;
    public string? Username { get; private set; }
    public List<string> Roles { get; private set; } = new();

    public bool IsAuthenticated => !string.IsNullOrEmpty(_jwtToken);

    public AuthService(string apiBaseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl)
        };
    }
    public async Task<bool> LoginAsync(LoginModel model)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "auth/login")
        {
            Content = JsonContent.Create(model)
        };


        request.Options.Set(
            new HttpRequestOptionsKey<BrowserRequestCredentials>("BrowserRequestCredentials"),
            BrowserRequestCredentials.Include
        );

        var response = await _http.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
            if (tokenResponse?.Token is not null)
            {
                _jwtToken = tokenResponse.Token;
                _tokenExpiration = tokenResponse.ExpiresUTC;
                NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
                return true;
            }
        }
        _jwtToken = null;
        _tokenExpiration = null;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        return false;
    }
    public async Task<bool> RefreshTokenAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "auth/refresh");

        request.Options.Set(
            new HttpRequestOptionsKey<BrowserRequestCredentials>("BrowserRequestCredentials"),
            BrowserRequestCredentials.Include
        );

        var response = await _http.SendAsync(request);

        if (response.IsSuccessStatusCode)
        {
            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
            if (tokenResponse?.Token is not null)
            {
                _jwtToken = tokenResponse.Token;
                _tokenExpiration = tokenResponse.ExpiresUTC;
                NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
                return true;
            }
        }
        _jwtToken = null;
        _tokenExpiration = null;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        return false;
    }
    public async Task LogoutAsync()
    {
        await _http.PostAsync("auth/logout", null);
        _jwtToken = null;
        _tokenExpiration = null;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public string? GetToken() => _jwtToken;
    public DateTime? GetTokenExpiration() => _tokenExpiration;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var identity = new ClaimsIdentity();
        Username = null;
        Roles.Clear();
        if (IsAuthenticated && _tokenExpiration > DateTime.UtcNow && !string.IsNullOrEmpty(_jwtToken))
        {
            var claims = ParseClaimsFromJwt(_jwtToken).ToList();
            identity = new ClaimsIdentity(claims, "jwt");

            Username = claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;
            Roles = claims
                .Where(c => c.Type == ClaimTypes.Role)
                .Select(c => c.Value)
                .ToList();
            _http.DefaultRequestHeaders.Authorization =
           new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwtToken);
        }
        else
        {
            _http.DefaultRequestHeaders.Authorization = null;
        }
        var user = new ClaimsPrincipal(identity);
        var state = new AuthenticationState(user);

        return state;
    }

    private List<Claim> ParseClaimsFromJwt(string jwt)
    {
        var claims = new List<Claim>();
        var payload = jwt.Split('.')[1];
        var jsonBytes = ParseBase64WithoutPadding(payload);
        var keyValuePairs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(jsonBytes);

        if (keyValuePairs != null)
        {
            foreach (var kvp in keyValuePairs)
            {
                claims.Add(new Claim(kvp.Key, kvp.Value.ToString() ?? ""));
            }
        }

        return claims;
    }
    private byte[] ParseBase64WithoutPadding(string base64)
    {
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }
}