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

await builder.Build().RunAsync();
