using GameTownApp;
using GameTownApp.Helpers;
using GameTownApp.Pages;
using GameTownApp.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");


var environment = builder.HostEnvironment.Environment;
var apiBaseUrl = environment =="Development" ? "https://localhost:7188" : "https://api.gametowndev.com";
builder.Services.AddScoped<AuthService>(provider => new AuthService(apiBaseUrl));
builder.Services.AddScoped<AuthenticationStateProvider>(provider => provider.GetRequiredService<AuthService>());
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<TokenRefreshHandler>();
builder.Services.AddBlazorBootstrap();
builder.Services.AddScoped(sp =>
{
    var authService = sp.GetRequiredService<AuthService>();
    var navigationManager = sp.GetRequiredService<NavigationManager>();
    var handler = new TokenRefreshHandler(authService, navigationManager)
    {
        InnerHandler = new CookieHandler()
    };
    return new HttpClient(handler)
    {
        BaseAddress = new Uri(apiBaseUrl)
    };
});
builder.Services.AddScoped<GamesService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<UploadService>();

var host = builder.Build();

// Restore the session before the first paint. The refresh_token cookie is HttpOnly, so the only way
// to know whether a returning visitor is signed in is to ask the API. Without this they render as
// anonymous — the Contribute and Administer nav sections stay hidden — until they happen to visit
// /login, which reads as the app having forgotten them.
//
// The catch is load-bearing: RefreshTokenAsync calls SendAsync unguarded and throws when the API is
// unreachable. That would happen here, before anything has rendered, and white-screen the whole app.
// Better to start anonymous than not to start. (No cookie is not an error — that returns 401 and the
// method simply reports false.)
try
{
    await host.Services.GetRequiredService<AuthService>().RefreshTokenAsync();
}
catch
{
    // API down or unreachable — carry on as an anonymous visitor.
}

await host.RunAsync();
