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


// The API serves this bundle, so the API *is* wherever the app was loaded from. This used to be a
// compile-time choice between a localhost port and a hard-coded hostname, which meant a build was
// tied to one deployment; resolving it at runtime is what lets one published artifact run on any
// LAN — someone else's install, a different port, a reverse proxy — with no rebuild.
var apiBaseUrl = builder.HostEnvironment.BaseAddress;
builder.Services.AddScoped<AuthService>(provider => new AuthService(apiBaseUrl));
builder.Services.AddScoped<AuthenticationStateProvider>(provider => provider.GetRequiredService<AuthService>());
builder.Services.AddAuthorizationCore();
builder.Services.AddBlazorBootstrap();
builder.Services.AddScoped(sp =>
{
    // Only CookieHandler remains. TokenRefreshHandler is gone: it existed to notice a JWT nearing
    // expiry and renew it before each call, and the auth cookie's sliding expiration now does that
    // server-side with nothing to schedule.
    //
    // CookieHandler stays even though same-origin fetch would send the cookie by default. It is one
    // line, and being explicit costs nothing next to the failure it prevents — requests that quietly
    // arrive anonymous, which looks like a permissions bug rather than a missing credential.
    return new HttpClient(new CookieHandler())
    {
        BaseAddress = new Uri(apiBaseUrl)
    };
});
builder.Services.AddScoped<GamesService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<UploadService>();
builder.Services.AddScoped<SettingsService>();

var host = builder.Build();

// Restore the session before the first paint. The auth cookie is HttpOnly, so the only way
// to know whether a returning visitor is signed in is to ask the API. Without this they render as
// anonymous — the Contribute and Administer nav sections stay hidden — until they happen to visit
// /login, which reads as the app having forgotten them.
//
// The catch is load-bearing: RefreshUserAsync calls SendAsync unguarded and throws when the API is
// unreachable. That would happen here, before anything has rendered, and white-screen the whole app.
// Better to start anonymous than not to start. (No cookie is not an error — that returns 401 and the
// method simply reports false.)
try
{
    await host.Services.GetRequiredService<AuthService>().RefreshUserAsync();
}
catch
{
    // API down or unreachable — carry on as an anonymous visitor.
}

await host.RunAsync();
