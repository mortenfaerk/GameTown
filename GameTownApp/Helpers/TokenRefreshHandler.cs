using GameTownApp.Services;
using Microsoft.AspNetCore.Components;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public class TokenRefreshHandler : DelegatingHandler
{
    private readonly AuthService _authService;
    private readonly NavigationManager _navigationManager;

    public TokenRefreshHandler(AuthService authService, NavigationManager navigationManager)
    {
        _authService = authService;
        _navigationManager = navigationManager;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_authService.IsAuthenticated && _authService.GetTokenExpiration() <= DateTime.UtcNow)
        {
            var refreshed = await _authService.RefreshTokenAsync();
            if (!refreshed)
            {
                _navigationManager.NavigateTo("/login", forceLoad: true);
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                {
                    RequestMessage = request
                };
                return response;
            }
        }

        var token = _authService.GetToken();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}