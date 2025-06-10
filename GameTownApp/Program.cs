using GameTownApp;
using GameTownApp.Helpers;
using GameTownApp.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

//if environment is dev use following url:
var environment = builder.HostEnvironment.Environment;
var apiBaseUrl = environment =="Development" ? "https://localhost:7188" : "https://api.gametowndev.com";
builder.Services.AddScoped(sp => new HttpClient(new CookieHandler()) { BaseAddress = new Uri(apiBaseUrl) });
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<AuthenticationStateProvider>(provider => provider.GetRequiredService<AuthService>());
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<GamesService>();
builder.Services.AddScoped<UserService>();

await builder.Build().RunAsync();
