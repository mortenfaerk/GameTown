using GameTownApp.Models.Auth;
using System.Net.Http.Json;

namespace GameTownApp.Services;

public class AuthService
{
    private readonly HttpClient _http;
    private string? _jwtToken;
    private DateTime? _tokenExpiration;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_jwtToken);
    

    public event Action? AuthStatusChanged;

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
                AuthStatusChanged?.Invoke();
                return true;
            }
        }
        _jwtToken = null;
        _tokenExpiration = null;
        AuthStatusChanged?.Invoke();
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
                AuthStatusChanged?.Invoke();
                return true;
            }
        }
        _jwtToken = null;
        _tokenExpiration = null;
        AuthStatusChanged?.Invoke();
        return false;
    }

    public async Task LogoutAsync()
    {
        await _http.PostAsync("auth/logout", null);
        _jwtToken = null;
        _tokenExpiration = null;
        AuthStatusChanged?.Invoke();
    }

    public string? GetToken() => _jwtToken;
}