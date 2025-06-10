using GameTownApp.Models.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Json;
using System.Security.Claims;

namespace GameTownApp.Services;

public class AuthService : AuthenticationStateProvider
{
    private readonly HttpClient _http;
    private string? _jwtToken;
    private DateTime? _tokenExpiration;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_jwtToken);

    public AuthService(HttpClient http)
    {
        _http = http;
    }

    public async Task<bool> LoginAsync(LoginModel model)
    {
        var response = await _http.PostAsJsonAsync("auth/login", model);
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
        var response = await _http.PostAsync("auth/refresh", null);
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

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var identity = new ClaimsIdentity();
        if(IsAuthenticated && _tokenExpiration > DateTime.UtcNow && !string.IsNullOrEmpty(_jwtToken))
        {
            identity = new ClaimsIdentity(ParseClaimsFromJwt(_jwtToken), "jwt");
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

    private IEnumerable<Claim> ParseClaimsFromJwt(string jwt)
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